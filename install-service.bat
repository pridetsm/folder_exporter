@echo off
rem ===========================================================================
rem  folder_exporter - install as a Windows service
rem
rem  Right-click this file and choose "Run as administrator", or run it from an
rem  elevated command prompt. It validates the configuration before installing
rem  anything, so a bad YAML file cannot leave you with a broken service.
rem
rem  Usage:  install-service.bat [service-name]
rem          (default service name: folder_exporter)
rem ===========================================================================
setlocal EnableExtensions EnableDelayedExpansion

set "SCRIPT_DIR=%~dp0"
set "EXE=%SCRIPT_DIR%folder_exporter.exe"
set "CONFIG=%SCRIPT_DIR%folder_exporter.yml"
set "SERVICE_NAME=folder_exporter"
if not "%~1"=="" set "SERVICE_NAME=%~1"

echo.
echo  ===========================================================
echo   folder_exporter - service installation
echo  ===========================================================
echo.
echo   Service name : %SERVICE_NAME%
echo   Executable   : %EXE%
echo   Config       : %CONFIG%
echo.

rem --- 1. the files must actually be here ------------------------------------
if not exist "%EXE%" (
    echo  [ERROR] folder_exporter.exe was not found next to this script.
    echo          Copy the whole release folder to this server and run it from there.
    goto :fail
)
if not exist "%CONFIG%" (
    echo  [ERROR] folder_exporter.yml was not found next to this script.
    echo          The service needs its configuration file alongside the exe.
    goto :fail
)

rem --- 2. administrator rights ----------------------------------------------
fltmc >nul 2>&1
if errorlevel 1 (
    echo  [WARN] This script needs administrator rights to register a service.
    echo.
    choice /C YN /N /M "     Relaunch elevated now? [Y/N] "
    if errorlevel 2 (
        echo.
        echo      Cancelled. Right-click this file and choose "Run as administrator".
        goto :fail
    )
    powershell -NoProfile -Command "Start-Process -FilePath '%~f0' -ArgumentList '%SERVICE_NAME%' -Verb RunAs"
    endlocal
    exit /b 0
)
echo  [OK] Running with administrator rights.

rem --- 3. the .NET Framework runtime must be present -------------------------
"%EXE%" --version >nul 2>&1
if errorlevel 1 (
    echo  [ERROR] folder_exporter.exe could not start.
    echo          The .NET Framework 4.x runtime is required. It ships with
    echo          Windows 10/11 and Server 2016+; on older builds install it first.
    goto :fail
)
for /f "delims=" %%v in ('"%EXE%" --version') do set "VERSION=%%v"
echo  [OK] %VERSION%

rem --- 4. the configuration must be valid ------------------------------------
echo.
echo  Validating configuration...
echo  -----------------------------------------------------------
"%EXE%" --config "%CONFIG%" --check-config
if errorlevel 1 (
    echo  -----------------------------------------------------------
    echo  [ERROR] The configuration is not valid. Fix folder_exporter.yml
    echo          and run this script again. Nothing has been installed.
    goto :fail
)
echo  -----------------------------------------------------------
echo  [OK] Configuration is valid.
echo.
echo  Any folder marked "path not found" above will report folder_exists 0
echo  until it appears. That is fine if the folder is created later.
echo.

rem --- 5. warn if the service would have nowhere to log ----------------------
rem  Done in PowerShell: a findstr pattern needing embedded quotes breaks cmd's
rem  redirection parsing. [char]34/39 keep this line free of nested quotes.
powershell -NoProfile -Command "$m = Select-String -Path '%CONFIG%' -Pattern '^log_file:' | Select-Object -First 1; if (-not $m) { exit 1 }; $v = ($m.Line.Substring(9) -replace '\s+#.*$', '').Trim().Trim([char]34).Trim([char]39); if ($v.Length -gt 0) { exit 0 } else { exit 1 }"
if errorlevel 1 (
    echo  [WARN] log_file is not set in folder_exporter.yml. A service has no
    echo      console, so nothing will be logged. Consider setting:
    echo          log_file: C:\ProgramData\folder_exporter\exporter.log
    echo.
)

rem --- 6. read the listen port out of the YAML, for the messages below -------
set "PORT=9847"
for /f "tokens=2,3 delims=:" %%a in ('findstr /b /c:"listen_address:" "%CONFIG%" 2^>nul') do (
    set "PORTRAW=%%b"
)
if defined PORTRAW (
    for /f "tokens=1" %%p in ("!PORTRAW!") do set "PORT=%%p"
    set "PORT=!PORT:"=!"
    set "PORT=!PORT:'=!"
)

rem --- 7. handle an existing installation ------------------------------------
sc query "%SERVICE_NAME%" >nul 2>&1
if not errorlevel 1 (
    echo  [WARN] A service named "%SERVICE_NAME%" is already installed.
    echo.
    choice /C YN /N /M "     Stop, remove and reinstall it? [Y/N] "
    if errorlevel 2 (
        echo.
        echo      Cancelled. Nothing was changed.
        goto :fail
    )
    echo.
    echo  Removing the existing service...
    "%EXE%" --uninstall --service-name "%SERVICE_NAME%" >nul 2>&1
    sc delete "%SERVICE_NAME%" >nul 2>&1
    rem Give the SCM a moment to release the name.
    ping -n 3 127.0.0.1 >nul
)

rem --- 8. install ------------------------------------------------------------
echo  Installing the service...
"%EXE%" --install --config "%CONFIG%" --service-name "%SERVICE_NAME%"
if errorlevel 1 (
    echo  [ERROR] Service installation failed. See the message above.
    goto :fail
)

rem --- 9. start it -----------------------------------------------------------
echo.
echo  Starting the service...
sc start "%SERVICE_NAME%" >nul 2>&1
rem Poll briefly: the SCM returns before the service is actually running.
set "STATE=UNKNOWN"
for /l %%i in (1,1,15) do (
    ping -n 2 127.0.0.1 >nul
    for /f "tokens=3" %%s in ('sc query "%SERVICE_NAME%" ^| findstr /c:"STATE"') do set "STATE=%%s"
    if "!STATE!"=="4" goto :running
)

echo  [ERROR] The service did not reach the RUNNING state.
echo          Check the log file, and Event Viewer ^> Windows Logs ^> Application.
echo          Run "%EXE%" --config "%CONFIG%" from this prompt to see the error directly.
goto :fail

:running
echo  [OK] Service "%SERVICE_NAME%" is running.

rem --- 10. verify it is actually serving metrics -----------------------------
echo.
echo  Checking the metrics endpoint...
powershell -NoProfile -Command "try { $r = Invoke-WebRequest 'http://localhost:%PORT%/metrics' -UseBasicParsing -TimeoutSec 10; $n = ([regex]::Matches($r.Content, 'folder_exists\{')).Count; Write-Host ('  [OK] HTTP ' + $r.StatusCode + ' from /metrics, ' + $n + ' folders monitored') } catch { Write-Host ('  [WARN] Could not read /metrics: ' + $_.Exception.Message) }"

rem --- 11. offer the firewall rule -------------------------------------------
echo.
netsh advfirewall firewall show rule name="Prometheus folder_exporter" >nul 2>&1
if errorlevel 1 (
    echo  Prometheus must be able to reach port %PORT% on this server.
    choice /C YN /N /M "     Add an inbound firewall rule for TCP %PORT% now? [Y/N] "
    if errorlevel 2 (
        echo.
        echo      Skipped. To add it later, run:
        echo        netsh advfirewall firewall add rule name="Prometheus folder_exporter" dir=in action=allow protocol=TCP localport=%PORT%
    ) else (
        netsh advfirewall firewall add rule name="Prometheus folder_exporter" dir=in action=allow protocol=TCP localport=%PORT% >nul
        if errorlevel 1 (
            echo      [WARN] Could not add the firewall rule.
        ) else (
            echo      [OK] Firewall rule added for TCP %PORT%.
            echo      [WARN] It allows any source address. Narrow it to your Prometheus server with:
            echo          netsh advfirewall firewall set rule name="Prometheus folder_exporter" new remoteip=10.0.0.0/8
        )
    )
) else (
    echo  [OK] A firewall rule named "Prometheus folder_exporter" already exists.
)

rem --- done ------------------------------------------------------------------
echo.
echo  ===========================================================
echo   Installation complete
echo  ===========================================================
echo.
echo   Status page   http://localhost:%PORT%/
echo   Metrics       http://localhost:%PORT%/metrics
echo.
echo   Manage it with:
echo     sc query   %SERVICE_NAME%
echo     sc stop    %SERVICE_NAME%
echo     sc start   %SERVICE_NAME%
echo.
echo   To change which folders are monitored, edit
echo     %CONFIG%
echo   and save it. The service reloads automatically - no restart needed.
echo.
echo   On your Prometheus server, add to prometheus.yml:
echo.
echo     scrape_configs:
echo       - job_name: folder_exporter
echo         scrape_interval: 30s
echo         static_configs:
echo           - targets: ['%COMPUTERNAME%:%PORT%']
echo.
echo   To remove the service later, run uninstall-service.bat
echo.
pause
endlocal
exit /b 0

:fail
echo.
pause
endlocal
exit /b 1
