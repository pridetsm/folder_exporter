<#
.SYNOPSIS
    End-to-end self test: builds a temporary folder tree, runs the exporter
    against it, and asserts that the exposed metrics are correct and that the
    output is valid Prometheus exposition format.

.EXAMPLE
    .\selftest.ps1
#>
[CmdletBinding()]
param(
    [string]$Exe  = "$PSScriptRoot\releases\folder_exporter.exe",
    [int]$Port    = 19847
)

$ErrorActionPreference = 'Stop'
$fail = 0
$pass = 0

function Assert($condition, $message) {
    if ($condition) { $script:pass++; Write-Host "  PASS  $message" -ForegroundColor Green }
    else            { $script:fail++; Write-Host "  FAIL  $message" -ForegroundColor Red }
}

if (-not (Test-Path $Exe)) { throw "exporter not built. Run .\build.ps1 first." }

$root = Join-Path ([System.IO.Path]::GetTempPath()) ("fe_selftest_" + [guid]::NewGuid().ToString('N').Substring(0,8))
$data = Join-Path $root 'data'
New-Item -ItemType Directory -Path "$data\sub" -Force | Out-Null
New-Item -ItemType Directory -Path "$data\skipme" -Force | Out-Null

1..4 | ForEach-Object { [System.IO.File]::WriteAllText("$data\f$_.csv", ('x' * (10 * $_))) }
[System.IO.File]::WriteAllText("$data\ignore.tmp", 'tmp')
[System.IO.File]::WriteAllText("$data\sub\nested.csv", ('y' * 500))
[System.IO.File]::WriteAllText("$data\skipme\hidden.csv", 'no')
[System.IO.File]::SetLastWriteTime("$data\f1.csv", (Get-Date).AddDays(-10))

# Deliberately exercises a spread of YAML forms: nested maps, block sequences of
# scalars, flow sequences, quoted and unquoted Windows paths, and inline labels.
$yml = @"
listen_address: 127.0.0.1:$Port
scan_interval_seconds: 2
log_level: warn
file_age_buckets_seconds: [300, 3600, 86400]

defaults:
  exclude:
    - "*.tmp"
  exclude_directories: ['skipme']
  expose_filename_labels: true
  change_tracking_mode: name

folders:
  - name: selftest
    path: $data
    labels:
      env: test
  - name: missing
    path: $root\does_not_exist
"@
$cfgPath = Join-Path $root 'folder_exporter.yml'
[System.IO.File]::WriteAllText($cfgPath, $yml)

# Runs --check-config out of process so native stderr cannot trip -ErrorAction Stop.
function Check-Config([string]$path) {
    $p = Start-Process -FilePath $Exe -ArgumentList @('--config', $path, '--check-config') `
                       -PassThru -Wait -WindowStyle Hidden `
                       -RedirectStandardOutput "$root\chk.out" -RedirectStandardError "$root\chk.err"
    return $p.ExitCode
}

Write-Host "`n== yaml config parsing ==" -ForegroundColor Cyan
Assert ((Check-Config $cfgPath) -eq 0) "--check-config accepts a valid YAML config"

# Each of these must be rejected with a non-zero exit code.
$badConfigs = [ordered]@{
    'a UNC path'                = "folders:`n  - path: \\fileserver\share"
    'a relative path'           = "folders:`n  - path: data\inbound"
    'tab indentation'           = "folders:`n`t- path: C:\data"
    'a duplicate key'           = "log_level: info`nlog_level: warn`nfolders:`n  - path: C:\data"
    'a duplicate folder name'   = "folders:`n  - name: a`n    path: C:\data`n  - name: a`n    path: C:\temp"
    'no folders section'        = "listen_address: 0.0.0.0:9847"
    'a non-boolean flag'        = "folders:`n  - path: C:\data`n    recursive: maybe"
    'a non-numeric interval'    = "scan_interval_seconds: soon`nfolders:`n  - path: C:\data"
    'a reserved label name'     = "folders:`n  - path: C:\data`n    labels:`n      path: nope"
    'a folder with no path'     = "folders:`n  - name: x"
}
$badPath = Join-Path $root 'bad.yml'
foreach ($case in $badConfigs.GetEnumerator()) {
    [System.IO.File]::WriteAllText($badPath, $case.Value)
    Assert ((Check-Config $badPath) -ne 0) "config is rejected: $($case.Key)"
}

Write-Host "`n== starting exporter ==" -ForegroundColor Cyan
$proc = Start-Process -FilePath $Exe -ArgumentList @('--config', $cfgPath) -PassThru -WindowStyle Hidden `
                      -RedirectStandardOutput "$root\out.log" -RedirectStandardError "$root\err.log"
try {
    $up = $false
    for ($i = 0; $i -lt 40; $i++) {
        Start-Sleep -Milliseconds 250
        try { Invoke-WebRequest "http://127.0.0.1:$Port/healthz" -UseBasicParsing -TimeoutSec 2 | Out-Null; $up = $true; break } catch { }
    }
    Assert $up "exporter answers /healthz"
    Start-Sleep -Seconds 3

    function Get-Metric($text, $name, $target) {
        $rx = [regex]::new('(?m)^' + [regex]::Escape($name) + '\{[^}]*target="' + [regex]::Escape($target) + '"[^}]*\}\s+(\S+)$')
        $m = $rx.Match($text)
        if ($m.Success) { return [double]$m.Groups[1].Value }
        return $null
    }

    $body = (Invoke-WebRequest "http://127.0.0.1:$Port/metrics" -UseBasicParsing).Content

    Write-Host "`n== metric correctness ==" -ForegroundColor Cyan
    Assert ((Get-Metric $body 'folder_files' 'selftest') -eq 5)        "folder_files counts 5 files (.tmp and excluded dir skipped)"
    Assert ((Get-Metric $body 'folder_directories' 'selftest') -eq 1)  "folder_directories counts 1 subdirectory"
    Assert ((Get-Metric $body 'folder_size_bytes' 'selftest') -eq 600) "folder_size_bytes totals 600 bytes"
    Assert ((Get-Metric $body 'folder_largest_file_bytes' 'selftest') -eq 500) "folder_largest_file_bytes is 500"
    Assert ((Get-Metric $body 'folder_up' 'selftest') -eq 1)           "folder_up is 1 for an existing folder"
    Assert ((Get-Metric $body 'folder_exists' 'missing') -eq 0)        "folder_exists is 0 for a missing folder"
    $oldest = Get-Metric $body 'folder_oldest_file_age_seconds' 'selftest'
    Assert ($oldest -gt 860000 -and $oldest -lt 870000)                "folder_oldest_file_age_seconds reports ~10 days"
    Assert ((Get-Metric $body 'folder_age_seconds' 'selftest') -ne $null) "folder_age_seconds is exposed"
    Assert ((Get-Metric $body 'folder_volume_free_bytes' 'selftest') -gt 0) "folder_volume_free_bytes is exposed"
    Assert ($body -match 'le="86400"')                                 "custom file_age_buckets_seconds are applied"

    Write-Host "`n== change detection ==" -ForegroundColor Cyan
    [System.IO.File]::WriteAllText("$data\arrived.csv", 'new')
    [System.IO.File]::Delete("$data\f4.csv")
    Start-Sleep -Seconds 5
    $body2 = (Invoke-WebRequest "http://127.0.0.1:$Port/metrics" -UseBasicParsing).Content
    Assert ((Get-Metric $body2 'folder_files_added_total' 'selftest') -eq 1)   "one added file is counted"
    Assert ((Get-Metric $body2 'folder_files_removed_total' 'selftest') -eq 1) "one removed file is counted"
    Assert ((Get-Metric $body2 'folder_last_file_added_timestamp_seconds' 'selftest') -gt 0)   "last-added timestamp is set"
    Assert ((Get-Metric $body2 'folder_last_file_removed_timestamp_seconds' 'selftest') -gt 0) "last-removed timestamp is set"
    Assert ($body2 -match 'folder_last_added_file_info\{[^}]*file="arrived\.csv"')  "added filename is exposed"
    Assert ($body2 -match 'folder_last_removed_file_info\{[^}]*file="f4\.csv"')     "removed filename is exposed"

    Write-Host "`n== exposition format ==" -ForegroundColor Cyan
    $lines   = $body2 -split "`n" | Where-Object { $_.Trim().Length -gt 0 }
    $sample  = [regex]'^(?<n>[a-zA-Z_:][a-zA-Z0-9_:]*)(\{(?<l>.*)\})?\s(?<v>-?(\d+(\.\d+)?([eE][-+]?\d+)?|NaN|[-+]Inf))$'
    $bad     = @(); $seen = @{}; $order = @(); $lastName = ''
    $helpSeen = @{}; $typeSeen = @{}; $dupMeta = @()
    foreach ($l in $lines) {
        if ($l.StartsWith('# HELP ')) { $n = ($l -split ' ')[2]; if ($helpSeen[$n]) { $dupMeta += $n }; $helpSeen[$n] = $true; continue }
        if ($l.StartsWith('# TYPE ')) { $n = ($l -split ' ')[2]; if ($typeSeen[$n]) { $dupMeta += $n }; $typeSeen[$n] = $true; continue }
        if ($l.StartsWith('#')) { continue }
        $m = $sample.Match($l.TrimEnd("`r"))
        if (-not $m.Success) { $bad += $l; continue }
        $n = $m.Groups['n'].Value
        if ($n -ne $lastName) { $order += $n; $lastName = $n }
        $key = $l.Substring(0, $l.LastIndexOf(' '))
        if ($seen[$key]) { $bad += "duplicate series: $key" }
        $seen[$key] = $true
    }
    Assert ($bad.Count -eq 0)      "every sample line is well formed and unique ($($lines.Count) lines)"
    if ($bad.Count) { $bad | Select-Object -First 5 | ForEach-Object { Write-Host "        $_" -ForegroundColor DarkRed } }
    Assert ($dupMeta.Count -eq 0)  "no duplicate # HELP / # TYPE declarations"
    $interleaved = ($order | Group-Object | Where-Object Count -gt 1)
    Assert ($interleaved.Count -eq 0) "samples of each metric name are contiguous"
    if ($interleaved.Count) { $interleaved | ForEach-Object { Write-Host "        $($_.Name)" -ForegroundColor DarkRed } }

    Write-Host "`n== http behaviour ==" -ForegroundColor Cyan
    $gz = Invoke-WebRequest "http://127.0.0.1:$Port/metrics" -UseBasicParsing -Headers @{ 'Accept-Encoding' = 'gzip' }
    Assert ($gz.Headers['Content-Encoding'] -eq 'gzip') "gzip is used when the client accepts it"
    Assert ((Invoke-WebRequest "http://127.0.0.1:$Port/" -UseBasicParsing).StatusCode -eq 200) "status page is served at /"
    $code = 0
    try { Invoke-WebRequest "http://127.0.0.1:$Port/nope" -UseBasicParsing | Out-Null } catch { $code = [int]$_.Exception.Response.StatusCode }
    Assert ($code -eq 404) "unknown paths return 404"
    Assert ((Invoke-WebRequest "http://127.0.0.1:$Port/-/reload" -Method POST -UseBasicParsing).StatusCode -eq 200) "POST /-/reload succeeds"

    Write-Host "`n== hot reload ==" -ForegroundColor Cyan
    [System.IO.File]::WriteAllText($cfgPath, $yml + "`n  - name: added_later`n    path: $data\sub`n")
    Start-Sleep -Seconds 4
    $body3 = (Invoke-WebRequest "http://127.0.0.1:$Port/metrics" -UseBasicParsing).Content
    Assert ((Get-Metric $body3 'folder_files' 'added_later') -ne $null) "a folder added to the yml is picked up without a restart"
    Assert ((Get-Metric $body3 'folder_files_added_total' 'selftest') -eq 1) "counters survive a reload"
    [System.IO.File]::WriteAllText($cfgPath, "this: [is: not: valid")
    Start-Sleep -Seconds 4
    $body4 = (Invoke-WebRequest "http://127.0.0.1:$Port/metrics" -UseBasicParsing).Content
    Assert ((Get-Metric $body4 'folder_files' 'selftest') -eq 5) "an invalid yml is rejected and the running config is kept"
    Assert ($body4 -match '(?m)^folder_exporter_config_reload_failures_total 1$') "the failed reload is counted"

    Write-Host "`n== resource footprint ==" -ForegroundColor Cyan
    $p = Get-Process -Id $proc.Id
    $ws = [math]::Round($p.WorkingSet64 / 1MB, 1)
    Write-Host ("        working set {0} MB, CPU {1:N2}s, {2} threads, {3} handles" -f $ws, $p.TotalProcessorTime.TotalSeconds, $p.Threads.Count, $p.Handles)
    Assert ($ws -lt 60) "working set stays under 60 MB"
}
finally {
    if ($proc -and -not $proc.HasExited) { Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue }
    Start-Sleep -Milliseconds 300
    try { Remove-Item $root -Recurse -Force -ErrorAction SilentlyContinue } catch { }
}

Write-Host ""
if ($fail -eq 0) { Write-Host "All $pass checks passed." -ForegroundColor Green; exit 0 }
Write-Host "$fail of $($pass + $fail) checks FAILED." -ForegroundColor Red
exit 1
