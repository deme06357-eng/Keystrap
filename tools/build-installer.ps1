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

# A running Keystrap locks its exe, but Windows still lets it be renamed out of the way. Old renamed copies from earlier
# builds pile up in dist, so clear the unlocked ones first; a copy still in use stays locked and is skipped.
function Move-RunningExe {
    Get-ChildItem $dist -Filter '*.old' -File -ErrorAction SilentlyContinue | ForEach-Object {
        try { Remove-Item $_.FullName -Force -ErrorAction Stop } catch { }
    }
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
    # Zips a copy: a running Keystrap locks its DLLs in dist, and Compress-Archive can't read locked files.
    $stage = Join-Path $env:TEMP ('keystrap-zip-' + [guid]::NewGuid().ToString('n'))
    New-Item -ItemType Directory $stage | Out-Null
    try {
        $packaged | ForEach-Object { Copy-Item (Join-Path $dist $_) $stage }
        # A virus scanner often locks a freshly-built exe while it scans it. Wait until every staged file opens for
        # reading, then zip; retry for a while before giving up with a clear message.
        $zipped = $false
        for ($i = 0; $i -lt 20 -and -not $zipped; $i++) {
            try {
                foreach ($f in $packaged) { $h = [IO.File]::Open((Join-Path $stage $f), 'Open', 'Read', 'ReadWrite'); $h.Close() }
                if (Test-Path $zip) { Remove-Item $zip -Force }
                Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $zip -Force -ErrorAction Stop
                $zipped = $true
            }
            catch { Start-Sleep -Milliseconds 800 }
        }
        if (-not $zipped) { throw "Couldn't read the new files to zip - a virus scanner is probably locking Keystrap.exe. Add an exclusion for the Keystrap folder, or try again." }
    }
    finally {
        # Never fail the build over a leftover temp folder (a scanner may still hold a file in it).
        for ($i = 0; $i -lt 6 -and (Test-Path $stage); $i++) {
            try { Remove-Item -Recurse -Force $stage -ErrorAction Stop }
            catch { Start-Sleep -Milliseconds 400 }
        }
    }

    & $iscc /Q "/DAppVersion=$version" (Join-Path $root 'installer\keystrap.iss')
    if ($LASTEXITCODE -ne 0) { throw "Inno Setup failed ($LASTEXITCODE)" }
}
finally {
    Invoke-Build
}

"Built $zip"
"Built $(Join-Path $release "KeystrapSetup-$version.exe")"
