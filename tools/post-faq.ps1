# Posts each FAQ item to the Keystrap Discord FAQ channel as its own message, in order. The webhook URL comes from
# faqWebhook in secrets.json, so it stays out of git. Every run posts new messages; it doesn't replace earlier ones.
#
#   powershell -File tools\post-faq.ps1             post every item
#   powershell -File tools\post-faq.ps1 -DryRun     print the messages instead of sending them
param([switch]$DryRun, [int]$DelayMs = 600)
$ErrorActionPreference = 'Stop'

$root = Split-Path $PSScriptRoot -Parent

# Same wording as the FAQ on the About page in keystrap.html; change both together.
$faq = @(
    "**What does beta mean?**`nBeta means it will still have bugs and is still getting tested.",
    "**Is my Roblox account safe?**`nYes. Keystrap never asks for your Roblox password and never reads your login cookie. You sign in to Roblox the normal way.",
    "**Why does my antivirus flag Keystrap?**`nIt's a false alarm. Keystrap downloads Roblox and changes its files, which some antivirus tools flag by habit, the same way they flag other Roblox launchers. Keystrap is open source, so you can read exactly what it does."
)

function Get-FaqWebhook {
    $file = Join-Path $root 'secrets.json'
    if (-not (Test-Path $file)) { throw 'secrets.json not found.' }
    $url = (Get-Content $file -Raw | ConvertFrom-Json).faqWebhook
    if (-not $url) { throw 'faqWebhook is not set in secrets.json.' }
    return $url
}

# Sends one message. If Discord still answers 429, waits as long as it asks and tries once more.
function Send-FaqMessage([string]$Url, [string]$Text) {
    $json = @{ content = $Text; allowed_mentions = @{ parse = @() } } | ConvertTo-Json -Compress
    $body = [Text.Encoding]::UTF8.GetBytes($json)
    try {
        Invoke-RestMethod -Uri $Url -Method Post -ContentType 'application/json' -Body $body | Out-Null
    }
    catch {
        $response = $_.Exception.Response
        if (-not $response -or [int]$response.StatusCode -ne 429) { throw }
        $waitSeconds = 2
        $retryAfter = $response.Headers['Retry-After']
        if ($retryAfter) { $waitSeconds = [math]::Ceiling([double]$retryAfter) }
        Start-Sleep -Seconds $waitSeconds
        Invoke-RestMethod -Uri $Url -Method Post -ContentType 'application/json' -Body $body | Out-Null
    }
}

$url = if ($DryRun) { $null } else { Get-FaqWebhook }

$sent = 0
foreach ($item in $faq) {
    if ($DryRun) {
        $item
        ''
        continue
    }
    Send-FaqMessage -Url $url -Text $item
    $sent++
    "Posted $sent of $($faq.Count)"
    if ($sent -lt $faq.Count) { Start-Sleep -Milliseconds $DelayMs }
}
