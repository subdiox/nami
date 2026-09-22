# Release build: self-contained, Native AOT. Needs the .NET 10 SDK and
# Visual Studio Build Tools with the "Desktop development with C++" workload
# (winget install Microsoft.VisualStudio.BuildTools --override "--quiet --wait --add Microsoft.VisualStudio.Workload.VCTools --includeRecommended").
# Local builds carry version 0.0.0 ("dev"); CI passes the tag version.
param([string]$Version = "0.0.0")
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$vcvars = Get-ChildItem "${env:ProgramFiles(x86)}\Microsoft Visual Studio\*\*\VC\Auxiliary\Build\vcvars64.bat", "${env:ProgramFiles}\Microsoft Visual Studio\*\*\VC\Auxiliary\Build\vcvars64.bat" -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $vcvars) { throw "vcvars64.bat not found: install VS Build Tools with the C++ workload" }
$proj = Join-Path $root "src\Nami"
$sha = (git -C $root rev-parse --short HEAD 2>$null)
$info = if ($Version -eq "0.0.0") { "dev+$sha" } else { "$Version+$sha" }
$versionArgs = " -p:Version=$Version -p:FileVersion=$Version.0 -p:InformationalVersion=$info"
cmd /c "`"$($vcvars.FullName)`" >nul 2>&1 && dotnet publish `"$proj`" -c Release -p:Platform=x64 -p:PublishProfile=win-x64.pubxml -p:IlcUseEnvironmentalTools=true$versionArgs"
if ($LASTEXITCODE -ne 0) { throw "publish failed" }
Write-Host "Output: $proj\bin\Release\net10.0-windows10.0.26100.0\win-x64\publish"
