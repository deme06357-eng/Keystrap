# Posts new git commits to the developers' Discord channel in Keystrap's changelog style: author header,
# "Keystrap 1.x" title, a `commits` tag, one bullet per commit (short hash, message, files changed) and a
# one-line footer. Run by the post-commit hook in .githooks; it remembers the last commit it posted, so commits
# made while offline (or by a rebase or merge) go out together next time.
#
#   powershell -File tools\commit-changelog.ps1             post anything new since the last run
#   powershell -File tools\commit-changelog.ps1 -DryRun     print the message instead of sending it
#
# A commit whose message contains [skip changelog] is left out.
param([switch]$DryRun, [int]$Max = 10)
$ErrorActionPreference = 'Stop'

$root = Split-Path $PSScriptRoot -Parent

# git from the PATH; else the one running this hook (GIT_EXEC_PATH); else GitHub Desktop's bundled copy.
function Find-Git {
    $cmd = Get-Command git -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }
    if ($env:GIT_EXEC_PATH) {
        $bin = Join-Path (Split-Path (Split-Path $env:GIT_EXEC_PATH -Parent) -Parent) 'bin\git.exe'
        if (Test-Path $bin) { return $bin }
    }
    $desktop = Get-ChildItem (Join-Path $env:LOCALAPPDATA 'GitHubDesktop') -Directory -Filter 'app-*' -ErrorAction SilentlyContinue |
        Sort-Object { [version]($_.Name.Substring(4)) } -Descending |
        ForEach-Object { Join-Path $_.FullName 'resources\app\git\cmd\git.exe' } | Where-Object { Test-Path $_ } | Select-Object -First 1
    if ($desktop) { return $desktop }
    throw 'git not found'
}
Set-Alias git (Find-Git)

$gitDir = (& git -C $root rev-parse --git-dir).Trim()
if (-not [IO.Path]::IsPathRooted($gitDir)) { $gitDir = Join-Path $root $gitDir }
$stateFile = Join-Path $gitDir 'keystrap-changelog-last'
$logFile = Join-Path $gitDir 'keystrap-changelog.log'
function Log($text) { Add-Content $logFile ((Get-Date -Format 'yyyy-MM-dd HH:mm:ss') + '  ' + $text) -Encoding UTF8 }

try {
    # The same webhook the app uses (secrets.json, kept out of the repository), and the version from the app's code.
    $secretsFile = Join-Path $root 'secrets.json'
    if (-not (Test-Path $secretsFile)) { throw 'No secrets.json (copy secrets.example.json and fill it in)' }
    $url = (Get-Content $secretsFile -Raw -Encoding UTF8 | ConvertFrom-Json).notificationsWebhook
    if (-not $url) { throw 'secrets.json has no notificationsWebhook' }
    $program = Get-Content (Join-Path $root 'app\Program.cs') -Raw -Encoding UTF8
    $raw = [regex]::Match($program, 'public const string Version = "v?([0-9.]+)"').Groups[1].Value
    # Shown as major.minor: "v1" or "1" reads 1.0, "1.3" stays 1.3.
    $version = if ($raw -match '\.') { $raw } elseif ($raw) { "$raw.0" } else { '?' }

    # Commits since the last one posted, oldest first (only the newest one on the very first run).
    $head = (& git -C $root rev-parse HEAD).Trim()
    $last = if (Test-Path $stateFile) { (Get-Content $stateFile -Raw).Trim() } else { '' }
    if ($last -eq $head) { return }
    $known = $last -and ((& git -C $root cat-file -t $last 2>$null) -eq 'commit')
    $range = if ($known) { "$last..$head" } else { '-1' }
    $hashes = @(& git -C $root rev-list --reverse --no-merges $range $(if ($known) { $null } else { $head }) | Where-Object { $_ })
    if ($hashes.Count -eq 0) { Set-Content $stateFile $head; return }

    $lines = @(); $files = 0; $shown = 0
    foreach ($h in $hashes) {
        $subject = (& git -C $root log -1 --format=%s $h).Trim()
        if ($subject -match '\[skip changelog\]') { continue }
        $changed = @(& git -C $root diff-tree --no-commit-id --name-only -r --root $h | Where-Object { $_ }).Count
        $files += $changed
        $shown++
        if ($shown -le $Max) {
            $text = $subject.Replace('*', '\*').Replace('_', '\_').Replace('`', "'")
            if ($text.Length -gt 180) { $text = $text.Substring(0, 179) + '…' }
            $lines += '- `' + $h.Substring(0, 7) + '` **' + $text + '** · ' + $changed + $(if ($changed -eq 1) { ' file' } else { ' files' })
        }
    }
    if ($shown -eq 0) { Set-Content $stateFile $head; return }
    if ($shown -gt $Max) { $lines += '- …and ' + ($shown - $Max) + ' more' }

    $branch = (& git -C $root rev-parse --abbrev-ref HEAD).Trim()
    $count = if ($shown -eq 1) { '1 new commit' } else { "$shown new commits" }
    $body = '`commits · ' + $branch + '`' + "`n" + $count + ', ' + $files + $(if ($files -eq 1) { ' file' } else { ' files' }) + " changed`n`n" + ($lines -join "`n")
    $stamp = [DateTime]::UtcNow.ToString('MM/dd/yy, hh:mm tt', [Globalization.CultureInfo]::InvariantCulture)
    $payload = @{
        username = 'Keystrap'
        allowed_mentions = @{ parse = @() }
        embeds = @(@{
            author = @{ name = 'Keystrap Changelog' }
            title = "Keystrap $version"
            description = $body
            color = 0xFDE68A
            footer = @{ text = "Keystrap Commits | $stamp UTC" }
        })
    } | ConvertTo-Json -Depth 6

    if ($DryRun) { $payload; return }
    [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
    Invoke-RestMethod -Uri $url -Method Post -ContentType 'application/json; charset=utf-8' -Body ([Text.Encoding]::UTF8.GetBytes($payload)) -UserAgent 'Keystrap-Changelog' | Out-Null
    Set-Content $stateFile $head
    Log "Posted $shown commit(s) up to $($head.Substring(0, 7)) as Keystrap $version"
}
catch {
    Log ('Failed: ' + $_.Exception.Message)
    if ($DryRun) { throw }
}
