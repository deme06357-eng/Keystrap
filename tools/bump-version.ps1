# Moves version.txt to the next release: 1.2 -> 1.3, 1.9 -> 1.10. -Major starts a new line instead: 1.4 -> 2.0.
# The next app\build.ps1 carries it into both exes and keystrap.html; the changelog in keystrap.html is still yours to write.
param([switch]$Major)
$ErrorActionPreference = 'Stop'

$file = Join-Path (Split-Path $PSScriptRoot -Parent) 'version.txt'
$old = (Get-Content $file -TotalCount 1).Trim()
if ($old -notmatch '^(\d+)\.(\d+)$') { throw "version.txt should read like 1.2, not '$old'" }
$new = if ($Major) { ([int]$Matches[1] + 1).ToString() + '.0' } else { $Matches[1] + '.' + ([int]$Matches[2] + 1) }
Set-Content $file $new -Encoding ASCII
"v$old -> v$new"
