# Builds everything a release needs from the version in version.txt:
#   release\Keystrap.zip              what the in-app updater downloads
#   release\KeystrapSetup-<version>.exe   the installer, from installer\keystrap.iss
# Both hold a public build (no FAQ webhook, --dev does nothing). dist\ gets the normal build back afterwards, so the
# Keystrap (Developer) shortcut keeps working.
#
#   powershell -File tools\build-installer.ps1
#   powershell -File tools\build-installer.ps1 -WebView2Dir <extracted Microsoft.Web.WebView2 package>
param([string]$WebView2Dir = "$env:TEMP\keystrap-wv2\x")
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$dist = Join-Path $root 'dist'
$release = Join-Path $root 'release'

function Invoke-Build([switch]$Public) {
    $buildArgs = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', (Join-Path $root 'app\build.ps1'), '-WebView2Dir', $WebView2Dir)
    if ($Public) { $buildArgs += '-Public' }
    & powershell @buildArgs | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "app\build.ps1 failed ($LASTEXITCODE)" }
}

function Find-Iscc {
    $iscc = @(
        "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
        "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
        "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"
    ) | Where-Object { Test-Path $_ } | Select-Object -First 1
    if ($iscc) { return $iscc }
    $onPath = Get-Command ISCC.exe -ErrorAction SilentlyContinue
    if ($onPath) { return $onPath.Source }
    throw 'Inno Setup 6 is not installed. Get it from https://jrsoftware.org/isdl.php (or: winget install JRSoftware.InnoSetup).'
}

# A running Keystrap locks its exe, but Windows still lets it be renamed out of the way.
function Move-RunningExe {
    $exe = Join-Path $dist 'Keystrap.exe'
    if (Test-Path $exe) { Rename-Item $exe ('Keystrap.exe.running' + (Get-Date -Format 'HHmmss') + '.old') }
}

$version = (Get-Content (Join-Path $root 'version.txt') -Raw).Trim()
if ($version -notmatch '^\d+\.\d+\.\d+$') { throw "version.txt should hold a version like 1.3.10, not '$version'" }
if (-not (Test-Path (Join-Path $WebView2Dir 'lib\net462'))) { throw "No WebView2 package at $WebView2Dir. Pass its folder with -WebView2Dir." }
$iscc = Find-Iscc

Move-RunningExe
try {
    Invoke-Build -Public

    $packaged = 'Keystrap.exe', 'Keystrap.Updater.exe', 'Microsoft.Web.WebView2.Core.dll', 'Microsoft.Web.WebView2.WinForms.dll', 'WebView2Loader.dll'
    New-Item -ItemType Directory -Force $release | Out-Null
    $zip = Join-Path $release 'Keystrap.zip'
    if (Test-Path $zip) { Remove-Item $zip }
    Compress-Archive -Path ($packaged | ForEach-Object { Join-Path $dist $_ }) -DestinationPath $zip

    & $iscc /Q "/DAppVersion=$version" (Join-Path $root 'installer\keystrap.iss')
    if ($LASTEXITCODE -ne 0) { throw "Inno Setup failed ($LASTEXITCODE)" }
}
finally {
    Invoke-Build
}

"Built $zip"
"Built $(Join-Path $release "KeystrapSetup-$version.exe")"
