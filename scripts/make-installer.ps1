# Publish (Native AOT) and build the Inno Setup installer locally.
#   scripts\make-installer.ps1 -Version 0.1.0
param([string]$Version = "0.0.0")
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot

& (Join-Path $PSScriptRoot "publish.ps1") -Version $Version

$iscc = @(
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) { throw "ISCC.exe not found: winget install JRSoftware.InnoSetup" }

& $iscc "/DAppVersion=$Version" (Join-Path $root "installer\Nami.iss")
if ($LASTEXITCODE -ne 0) { throw "ISCC failed" }
Get-ChildItem (Join-Path $root "installer\Output")
