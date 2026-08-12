@echo off
rem ===========================================================================
rem  folder_exporter - remove the Windows service
rem
rem  Right-click and "Run as administrator", or run from an elevated prompt.
rem  Only the service registration is removed; the exe, the YAML config and any
rem  log files are left untouched.
rem
rem  Usage:  uninstall-service.bat [service-name]
rem ===========================================================================
setlocal EnableExtensions EnableDelayedExpansion

set "SCRIPT_DIR=%~dp0"
set "EXE=%SCRIPT_DIR%folder_exporter.exe"
set "SERVICE_NAME=folder_exporter"
if not "%~1"=="" set "SERVICE_NAME=%~1"

echo.
echo  ===========================================================
echo   folder_exporter - remove service "%SERVICE_NAME%"
echo  ===========================================================
echo.

fltmc >nul 2>&1
if errorlevel 1 (
    echo  [WARN] This script needs administrator rights.
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

sc query "%SERVICE_NAME%" >nul 2>&1
if errorlevel 1 (
    echo  [OK] No service named "%SERVICE_NAME%" is installed. Nothing to do.
    echo.
    pause
    endlocal
    exit /b 0
)

echo  Stopping the service...
sc stop "%SERVICE_NAME%" >nul 2>&1
ping -n 3 127.0.0.1 >nul

echo  Removing the service...
if exist "%EXE%" (
    "%EXE%" --uninstall --service-name "%SERVICE_NAME%"
) else (
    sc delete "%SERVICE_NAME%"
)
if errorlevel 1 (
    echo  [ERROR] Removal failed. See the message above.
    goto :fail
)

echo.
echo  [OK] Service "%SERVICE_NAME%" removed.
echo.
echo  The executable, folder_exporter.yml and any log files were left in place.
echo  If you added a firewall rule for this exporter and no longer need it:
echo    netsh advfirewall firewall delete rule name="Prometheus folder_exporter"
echo.
pause
endlocal
exit /b 0

:fail
echo.
pause
endlocal
exit /b 1
