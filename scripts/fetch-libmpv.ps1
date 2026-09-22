# Downloads the latest libmpv x86_64 dev build from shinchiro/mpv-winbuild-cmake
# and drops libmpv-2.dll + headers into third_party/libmpv.
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$dest = Join-Path $root "third_party\libmpv"
$sevenZip = "C:\Program Files\7-Zip\7z.exe"
if (-not (Test-Path $sevenZip)) { throw "7-Zip not found at $sevenZip (winget install 7zip.7zip)" }

$release = Invoke-RestMethod "https://api.github.com/repos/shinchiro/mpv-winbuild-cmake/releases/latest"
$asset = $release.assets | Where-Object { $_.name -like "mpv-dev-x86_64-2*" } | Select-Object -First 1
if (-not $asset) { throw "no mpv-dev-x86_64 asset in release $($release.tag_name)" }

$tmp = Join-Path $env:TEMP "nami-libmpv"
New-Item -ItemType Directory -Force $tmp | Out-Null
$archive = Join-Path $tmp $asset.name
Write-Host "Downloading $($asset.name) ..."
Invoke-WebRequest $asset.browser_download_url -OutFile $archive
& $sevenZip x -y "-o$tmp\x" $archive | Out-Null

New-Item -ItemType Directory -Force (Join-Path $dest "include") | Out-Null
Copy-Item (Join-Path $tmp "x\libmpv-2.dll") $dest -Force
Copy-Item (Join-Path $tmp "x\include\mpv") (Join-Path $dest "include") -Recurse -Force
"$($asset.name)" | Set-Content (Join-Path $dest "VERSION.txt")
Write-Host "libmpv installed to $dest ($($asset.name))"
