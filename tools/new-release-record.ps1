param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^[0-9]+\.[0-9]+\.[0-9]+$')]
    [string] $Version,

    [string] $Package
)

$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$directory = Join-Path $root 'release-handoff'
New-Item -ItemType Directory -Path $directory -Force | Out-Null

$record = Join-Path $directory "release-$Version.evidence.json"
if (Test-Path -LiteralPath $record) {
    throw "Verification record already exists: $record"
}
$revision = (& git -C $root rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0 -or $revision -notmatch '^[a-fA-F0-9]{40}$') {
    throw 'Could not determine the current source revision.'
}

$packageHash = ''
if ($Package) {
    $file = (Resolve-Path -LiteralPath $Package -ErrorAction Stop).Path
    $packageHash = (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash.ToLowerInvariant()
}
$recordData = [ordered]@{
    version = $Version
    sourceCommit = $revision.ToLowerInvariant()
    packageSha256 = $packageHash
    japanese = [ordered]@{
        imageSha256 = ''
        tests = 'not-run'
        checkedAtUtc = ''
    }
    english = [ordered]@{
        imageSha256 = ''
        tests = 'not-run'
        checkedAtUtc = ''
    }
    pcsx2 = [ordered]@{
        version = ''
        japaneseModifiedBoot = 'not-run'
        englishModifiedBoot = 'not-run'
        smokeTestNotes = ''
        checkedAtUtc = ''
    }
    packageContentsReviewed = $false
    licenseCreditsReviewed = $false
    releaseNotesReviewed = $false
}
$recordData | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $record -Encoding utf8
Write-Host "Created local verification record: $record"
Write-Host 'All test statuses start as not-run. Enter results only after performing each check.'
