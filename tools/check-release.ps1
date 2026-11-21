param(
    [Parameter(Mandatory = $true)]
    [string] $Version,
    [Parameter(Mandatory = $true)]
    [string] $Package,
    [Parameter(Mandatory = $true)]
    [string] $Evidence
)

$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$packagePath = (Resolve-Path -LiteralPath $Package -ErrorAction Stop).Path
$evidencePath = (Resolve-Path -LiteralPath $Evidence -ErrorAction Stop).Path

if ($Version -notmatch '^0\.[0-9]+\.[0-9]+$') {
    throw 'Version must use the major.minor.patch format, for example 0.119.0.'
}
$head = (& git -C $repo rev-parse HEAD)
if ($LASTEXITCODE -ne 0 -or $head -notmatch '^[a-fA-F0-9]{40}$') {
    throw 'Could not identify the source commit.'
}
$head = $head.Trim().ToLowerInvariant()

$data = Get-Content -LiteralPath $evidencePath -Raw | ConvertFrom-Json
if ($null -eq $data -or $data.version -ne $Version) {
    throw 'Verification record has the wrong release version.'
}
if ($data.sourceCommit -ne $head) {
    throw 'Verification record does not match this source commit.'
}
$sha = (Get-FileHash -LiteralPath $packagePath -Algorithm SHA256).Hash.ToLowerInvariant()
if ($data.packageSha256 -ne $sha) {
    throw 'The packaged application does not match the reviewed SHA-256.'
}

foreach ($edition in @('japanese', 'english')) {
    $check = $data.$edition
    if ($null -eq $check -or $check.tests -ne 'passed' -or
        $check.imageSha256 -notmatch '^[a-fA-F0-9]{64}$') {
        throw "Missing passing real-disc tests or image hash for $edition."
    }
    if ([string]::IsNullOrWhiteSpace([string]$check.checkedAtUtc)) {
        throw "Missing real-disc test date for $edition."
    }
    $checked = [DateTimeOffset]::MinValue
    if (-not [DateTimeOffset]::TryParse([string]$check.checkedAtUtc, [ref]$checked)) {
        throw "Invalid real-disc test date for $edition."
    }
    $days = ([DateTimeOffset]::UtcNow - $checked.ToUniversalTime()).TotalDays
    if ($days -lt -1 -or $days -gt 30) {
        throw "The real-disc test for $edition is not within the last 30 days."
    }
}

$pcsx2 = $data.pcsx2
if ($null -eq $pcsx2 -or
    $pcsx2.japaneseModifiedBoot -ne 'passed' -or
    $pcsx2.englishModifiedBoot -ne 'passed' -or
    [string]::IsNullOrWhiteSpace([string]$pcsx2.version) -or
    [string]::IsNullOrWhiteSpace([string]$pcsx2.smokeTestNotes)) {
    throw 'PCSX2 verification is missing for one or both modified game editions.'
}
$smokeDate = [DateTimeOffset]::MinValue
if (-not [DateTimeOffset]::TryParse([string]$pcsx2.checkedAtUtc, [ref]$smokeDate)) {
    throw 'Missing or invalid PCSX2 test date.'
}
$smokeAge = ([DateTimeOffset]::UtcNow - $smokeDate.ToUniversalTime()).TotalDays
if ($smokeAge -lt -1 -or $smokeAge -gt 30) {
    throw 'PCSX2 testing must be repeated within 30 days of the release.'
}

foreach ($name in @('packageContentsReviewed', 'licenseCreditsReviewed', 'releaseNotesReviewed')) {
    if ($data.$name -ne $true) {
        throw "Release check not completed: $name."
    }
}

Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [System.IO.Compression.ZipFile]::OpenRead($packagePath)
try {
    $entries = @($archive.Entries | Where-Object {
        -not [string]::IsNullOrWhiteSpace($_.Name)
    } | ForEach-Object { $_.Name })
    $allowed = @('Berwick Workshop.exe', 'README.txt', 'Licenses.txt')
    if ($entries.Count -ne 3 -or
        @(Compare-Object ($entries | Sort-Object) ($allowed | Sort-Object)).Count -gt 0) {
        throw 'The release archive must contain the executable, README and Licenses files only.'
    }
    foreach ($entry in $archive.Entries) {
        $path = $entry.FullName.Replace('\', '/')
        if ($path -match '(^|/)\.\.(/|$)' -or $path -match '(^|/)\.(git|vs)(/|$)') {
            throw 'The archive contains an unsafe or private path.'
        }
    }
}
finally {
    $archive.Dispose()
}
Write-Host "Release $Version verification record and package checks passed."
Write-Host "Source commit: $head"
Write-Host "Package SHA-256: $sha"
