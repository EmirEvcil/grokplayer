$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$outDir = Join-Path $root "native\libmpv"
$version = Get-Content (Join-Path $PSScriptRoot "libmpv-version.txt") | Where-Object { $_ -like "url=*" }
$url = $version.Substring(4)
$seven = Join-Path $PSScriptRoot "7zr.exe"
$archive = Join-Path $env:TEMP "mpv-dev-lgpl-x86_64.7z"

New-Item -ItemType Directory -Force -Path $outDir | Out-Null
if (-not (Test-Path $seven)) {
    Invoke-WebRequest -Uri "https://github.com/ip7z/7zip/releases/download/26.02/7zr.exe" -OutFile $seven -UseBasicParsing
}

Write-Host "Downloading $url"
Invoke-WebRequest -Uri $url -OutFile $archive -UseBasicParsing
& $seven x $archive "-o$outDir" -y
Write-Host "libmpv ready in $outDir"
