# Moves version.txt to the next release: 1.0.8 -> 1.0.9, 1.0.9 -> 1.0.10.
# -Minor starts a new minor line (1.0.12 -> 1.1.0), -Major a new major one (1.4.2 -> 2.0.0).
# The next app\build.ps1 carries it into both exes and keystrap.html; the changelog in keystrap.html is still yours to write.
param([switch]$Minor, [switch]$Major)
$ErrorActionPreference = 'Stop'

$file = Join-Path (Split-Path $PSScriptRoot -Parent) 'version.txt'
$old = (Get-Content $file -TotalCount 1).Trim()
if ($old -notmatch '^(\d+)\.(\d+)\.(\d+)$') { throw "version.txt should read like 1.0.8, not '$old'" }
$x, $y, $z = [int]$Matches[1], [int]$Matches[2], [int]$Matches[3]   # not $major/$minor: those are the switches
$new = if ($Major) { "$($x + 1).0.0" } elseif ($Minor) { "$x.$($y + 1).0" } else { "$x.$y.$($z + 1)" }
Set-Content $file $new -Encoding ASCII
"v$old -> v$new"
