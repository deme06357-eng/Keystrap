# Builds release\KeystrapSetup-<version>.exe from installer\keystrap.iss, using the version in version.txt.
# It packages what's already in dist\, so build first (or pass -Build to run app\build.ps1 here).
#
#   powershell -File tools\build-installer.ps1
#   powershell -File tools\build-installer.ps1 -Build -WebView2Dir "$env:TEMP\keystrap-wv2\x"
param(
    [switch]$Build,
    [string]$WebView2Dir
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent

if ($Build) {
    $buildArgs = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', (Join-Path $root 'app\build.ps1'))
    if ($WebView2Dir) { $buildArgs += @('-WebView2Dir', $WebView2Dir) }
    & powershell @buildArgs
    if ($LASTEXITCODE -ne 0) { throw "app\build.ps1 failed ($LASTEXITCODE)" }
}

$version = (Get-Content (Join-Path $root 'version.txt') -Raw).Trim()
if ($version -notmatch '^\d+\.\d+\.\d+$') { throw "version.txt should hold a version like 1.3.10, not '$version'" }

# Everything the installer ships has to exist, or Inno Setup stops halfway with a less helpful message.
$needed = 'dist\Keystrap.exe', 'dist\Keystrap.Updater.exe', 'dist\Microsoft.Web.WebView2.Core.dll',
          'dist\Microsoft.Web.WebView2.WinForms.dll', 'dist\WebView2Loader.dll', 'app\keystrap.ico'
$missing = $needed | Where-Object { -not (Test-Path (Join-Path $root $_)) }
if ($missing) { throw "Build Keystrap first (app\build.ps1). Missing: $($missing -join ', ')" }

# Inno Setup's compiler, wherever it was installed.
$iscc = @(
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) {
    $onPath = Get-Command ISCC.exe -ErrorAction SilentlyContinue
    if ($onPath) { $iscc = $onPath.Source }
}
if (-not $iscc) { throw 'Inno Setup 6 is not installed. Get it from https://jrsoftware.org/isdl.php (or: winget install JRSoftware.InnoSetup).' }

& $iscc /Q "/DAppVersion=$version" (Join-Path $root 'installer\keystrap.iss')
if ($LASTEXITCODE -ne 0) { throw "Inno Setup failed ($LASTEXITCODE)" }

$setup = Join-Path $root "release\KeystrapSetup-$version.exe"
"Built $setup ($('{0:N1}' -f ((Get-Item $setup).Length / 1MB)) MB)"
