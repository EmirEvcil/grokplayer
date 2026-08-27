$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root "src\Grok.Player.App\Grok.Player.App.csproj"
$outDir = Join-Path $root "dist\GrokPlayer"

if (-not (Test-Path (Join-Path $root "native\libmpv\libmpv-2.dll"))) {
    & (Join-Path $PSScriptRoot "fetch-libmpv.ps1")
}

dotnet publish $project -c Release -r win-x64 --self-contained true -p:Platform=x64 -p:PublishTrimmed=false -p:WindowsPackageType=None -p:WindowsAppSDKSelfContained=true -o $outDir
if ($LASTEXITCODE -ne 0) {
    throw "Publish failed."
}

$required = @("GrokPlayer.exe", "libmpv-2.dll", "MainWindow.xbf", "App.xbf")
foreach ($name in $required) {
    $path = Join-Path $outDir $name
    if (-not (Test-Path $path)) {
        throw "Publish is incomplete: missing $name. The exe will crash on startup."
    }
}

$pri = Get-ChildItem $outDir -Filter "*.pri" -File -ErrorAction SilentlyContinue
if (-not $pri) {
    throw "Publish is incomplete: no .pri resource file. The exe will crash on startup."
}

Write-Host "Portable build: $outDir\GrokPlayer.exe"
Write-Host "Copy the entire GrokPlayer folder to any 64-bit Windows 10/11 PC and run GrokPlayer.exe."
