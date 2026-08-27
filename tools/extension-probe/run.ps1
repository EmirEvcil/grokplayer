$ErrorActionPreference = "Stop"
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$root = Resolve-Path (Join-Path $here "..\..")
$ext = Join-Path $root "extension"
$profile = Join-Path $root "tools\extension-probe\chrome-profile"
$chrome = "C:\Program Files\Google\Chrome\Application\chrome.exe"
$video = if ($args[0]) { $args[0] } else { "https://www.youtube.com/watch?v=Qtl8lJwbd4g" }

New-Item -ItemType Directory -Force -Path $profile | Out-Null
Write-Host "Isolated profile: $profile"
Write-Host "Extension: $ext"
Write-Host "Filter DevTools console with: GrokPlayer"
Write-Host "Opening $video"

& $chrome `
  --user-data-dir=$profile `
  --disable-first-run-ui `
  --no-first-run `
  --no-default-browser-check `
  --auto-open-devtools-for-tabs `
  --disable-features=DisableLoadExtensionCommandLineSwitch `
  --enable-unsafe-extension-debugging `
  --disable-extensions-except=$ext `
  --load-extension=$ext `
  $video
