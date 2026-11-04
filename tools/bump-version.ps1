# Moves version.txt to the next release. The last number counts up to 9, then the middle one moves on:
# 1.4.8 -> 1.4.9 -> 1.5.0 -> 1.5.1 ... 1.5.9 -> 1.6.0.
# -Minor starts the next minor line straight away (1.1.4 -> 1.2.0), -Major a new major one (1.4.2 -> 2.0.0).
# The next app\build.ps1 carries it into both exes and keystrap.html; the changelog in keystrap.html is still yours to write.
param([switch]$Minor, [switch]$Major)
$ErrorActionPreference = 'Stop'

$LastPatch = 9   # the highest last number before the middle one moves on

$file = Join-Path (Split-Path $PSScriptRoot -Parent) 'version.txt'
$old = (Get-Content $file -TotalCount 1).Trim()
if ($old -notmatch '^(\d+)\.(\d+)\.(\d+)$') { throw "version.txt should read like 1.0.8, not '$old'" }
$x, $y, $z = [int]$Matches[1], [int]$Matches[2], [int]$Matches[3]   # not $major/$minor: those are the switches
$new = if ($Major) { "$($x + 1).0.0" }
       elseif ($Minor -or $z -ge $LastPatch) { "$x.$($y + 1).0" }
       else { "$x.$y.$($z + 1)" }
Set-Content $file $new -Encoding ASCII
"v$old -> v$new"
