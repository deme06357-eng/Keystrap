# Posts the Keystrap FAQ to its Discord channel, one embed per question, matching the in-app "Post FAQ" button
# (Dev/Testing). It edits the same messages on a second run: the message ids are kept in the same file the app uses,
# %LocalAppData%\Keystrap\faq-message.txt. The webhook URL comes from faqWebhook in secrets.json, so it stays out of git.
#
#   powershell -File tools\post-faq.ps1             post or refresh every item
#   powershell -File tools\post-faq.ps1 -DryRun     print the messages instead of sending them
param([switch]$DryRun, [int]$DelayMs = 600)
$ErrorActionPreference = 'Stop'

$root = Split-Path $PSScriptRoot -Parent
$idFile = Join-Path $env:LOCALAPPDATA 'Keystrap\faq-message.txt'

# Same wording as the FAQ on the About page in keystrap.html; change both together.
$faq = @(
    @{ q = 'What does beta mean?'; a = 'Beta means it will still have bugs and is still getting tested.' },
    @{ q = 'Is my Roblox account safe?'; a = 'Yes. Keystrap never asks for your Roblox password and never reads your login cookie. You sign in to Roblox the normal way.' },
    @{ q = 'Why does my antivirus flag Keystrap?'; a = "It's a false alarm. Keystrap downloads Roblox and changes its files, which some antivirus tools flag by habit, the same way they flag other Roblox launchers. Keystrap is open source, so you can read exactly what it does." }
)

function Get-FaqWebhook {
    $file = Join-Path $root 'secrets.json'
    if (-not (Test-Path $file)) { throw 'secrets.json not found.' }
    $url = (Get-Content $file -Raw | ConvertFrom-Json).faqWebhook
    if (-not $url) { throw 'faqWebhook is not set in secrets.json.' }
    return $url
}

function New-Payload($item) {
    @{
        username         = 'Keystrap'
        allowed_mentions = @{ parse = @() }
        embeds           = @(@{ title = $item.q; description = $item.a; color = 0xFDE68A })
    } | ConvertTo-Json -Depth 6 -Compress
}

# One request. On HTTP 429 it waits as long as Discord asks and tries once more. Returns the parsed response (with .id
# for a POST) or $null, and sets $script:Status to the HTTP status code.
function Invoke-Discord([string]$Method, [string]$Uri, [string]$Body) {
    for ($attempt = 0; $attempt -lt 2; $attempt++) {
        try {
            $params = @{ Uri = $Uri; Method = $Method; ContentType = 'application/json' }
            if ($Body) { $params.Body = [Text.Encoding]::UTF8.GetBytes($Body) }
            $result = Invoke-RestMethod @params
            $script:Status = 200
            return $result
        }
        catch {
            $resp = $_.Exception.Response
            $script:Status = if ($resp) { [int]$resp.StatusCode } else { 0 }
            if ($script:Status -eq 429 -and $attempt -eq 0) {
                $wait = 2; $retryAfter = if ($resp) { $resp.Headers['Retry-After'] } else { $null }
                if ($retryAfter) { $wait = [math]::Ceiling([double]$retryAfter) }
                Start-Sleep -Seconds $wait
                continue
            }
            if ($script:Status -eq 404 -and $Method -eq 'Patch') { return $null }   # the message is gone; caller posts a fresh one
            throw
        }
    }
}

if ($DryRun) {
    foreach ($item in $faq) { "**$($item.q)**"; $item.a; '' }
    return
}

$url = Get-FaqWebhook
$oldIds = if (Test-Path $idFile) { Get-Content $idFile | ForEach-Object { $_.Trim() } | Where-Object { $_ } } else { @() }
$oldIds = @($oldIds)
$ids = New-Object Collections.Generic.List[string]
$edited = $false

for ($i = 0; $i -lt $faq.Count; $i++) {
    if ($i -gt 0) { Start-Sleep -Milliseconds $DelayMs }
    $body = New-Payload $faq[$i]
    $existing = if ($i -lt $oldIds.Count) { $oldIds[$i] } else { $null }
    $id = $null
    if ($existing) {
        Invoke-Discord -Method Patch -Uri "$url/messages/$existing" -Body $body | Out-Null
        if ($script:Status -eq 200) { $id = $existing; $edited = $true }
    }
    if (-not $id) {
        $sent = Invoke-Discord -Method Post -Uri "$url`?wait=true" -Body $body
        $id = [string]$sent.id
    }
    $ids.Add($id)
    "$($i + 1) of $($faq.Count): $(if ($edited -and $id -eq $existing) { 'edited' } else { 'posted' })"
}

# The FAQ got shorter than last time: delete the messages it no longer needs.
for ($j = $oldIds.Count - 1; $j -ge $ids.Count; $j--) {
    Start-Sleep -Milliseconds $DelayMs
    Invoke-Discord -Method Delete -Uri "$url/messages/$($oldIds[$j])" | Out-Null
}

Set-Content $idFile -Value $ids -Encoding ascii
if ($edited) { 'Refreshed the FAQ messages in Discord.' } else { 'Posted the FAQ to Discord.' }
