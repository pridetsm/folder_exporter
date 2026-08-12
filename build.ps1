<#
.SYNOPSIS
    Builds folder_exporter and assembles the deployable bundle in .\releases.

.DESCRIPTION
    No SDK, no NuGet, no internet access required. The .NET Framework 4.x
    compiler (csc.exe) ships with every Windows 10/11 and Server 2016+
    installation, so this builds anywhere.

    The releases folder is what you copy to a server: the executable, the YAML
    config, and the Prometheus scrape config and alert rules.

.EXAMPLE
    .\build.ps1
    .\build.ps1 -Output C:\tools\folder_exporter
#>
[CmdletBinding()]
param(
    [string]$Output = "$PSScriptRoot\releases",
    [switch]$DebugBuild
)

$ErrorActionPreference = 'Stop'

$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path $csc)) {
    $csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe'
}
if (-not (Test-Path $csc)) {
    throw "csc.exe not found. The .NET Framework 4.x runtime is required (it ships with Windows 10/11 and Server 2016+)."
}

if (-not (Test-Path $Output)) { New-Item -ItemType Directory -Path $Output -Force | Out-Null }

$exe     = Join-Path $Output 'folder_exporter.exe'
$sources = Get-ChildItem -Path (Join-Path $PSScriptRoot 'src') -Filter *.cs | ForEach-Object { $_.FullName }
if (-not $sources) { throw "no source files found in $PSScriptRoot\src" }

# Only in-box framework assemblies - the exporter has no third-party dependencies.
$refs = @(
    'System.dll',
    'System.Core.dll',
    'System.ServiceProcess.dll'
) | ForEach-Object { "/r:$_" }

$flags = @(
    '/nologo',
    '/target:exe',
    '/platform:anycpu',
    '/langversion:5',
    '/warn:4',
    "/out:$exe"
)
if ($DebugBuild) { $flags += '/debug+'; $flags += '/define:DEBUG' } else { $flags += '/optimize+' }

Write-Host "Compiling $($sources.Count) source files -> $exe" -ForegroundColor Cyan
& $csc @flags @refs @sources
if ($LASTEXITCODE -ne 0) { throw "compilation failed with exit code $LASTEXITCODE" }

# ---- assemble the release bundle -------------------------------------------

# The config is only copied if absent, so a server's edited config is never
# overwritten by a rebuild.
$cfgSrc = Join-Path $PSScriptRoot 'folder_exporter.yml'
$cfgDst = Join-Path $Output 'folder_exporter.yml'
if (Test-Path $cfgSrc) {
    if (Test-Path $cfgDst) {
        Copy-Item $cfgSrc (Join-Path $Output 'folder_exporter.yml.example') -Force
        Write-Host "Kept existing config; reference copy written to folder_exporter.yml.example" -ForegroundColor Yellow
    } else {
        Copy-Item $cfgSrc $cfgDst
        Write-Host "Starter config: $cfgDst" -ForegroundColor Yellow
    }
}

$promSrc = Join-Path $PSScriptRoot 'prometheus'
$promDst = Join-Path $Output 'prometheus'
if (Test-Path $promSrc) {
    if (-not (Test-Path $promDst)) { New-Item -ItemType Directory -Path $promDst -Force | Out-Null }
    Copy-Item (Join-Path $promSrc '*.yml') $promDst -Force
}

foreach ($f in @('INSTALL.md', 'install-service.bat', 'uninstall-service.bat')) {
    $p = Join-Path $PSScriptRoot $f
    if (Test-Path $p) { Copy-Item $p $Output -Force }
}

$size = [math]::Round((Get-Item $exe).Length / 1KB, 1)
Write-Host "Build succeeded: $exe ($size KB)" -ForegroundColor Green
Write-Host ""
Write-Host "Release bundle ready in $Output" -ForegroundColor Cyan
Get-ChildItem $Output -Recurse -File | ForEach-Object {
    Write-Host ("  " + $_.FullName.Substring($Output.Length + 1))
}
Write-Host ""
Write-Host "Deploy: copy that folder to the server, edit folder_exporter.yml, then run" -ForegroundColor Cyan
Write-Host "  folder_exporter.exe --check-config"
Write-Host "  folder_exporter.exe --install      (elevated: registers the Windows service)"
