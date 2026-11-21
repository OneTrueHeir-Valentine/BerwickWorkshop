# Synthetic evidence only. This checks the release guard, not the game or PCSX2.
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$checker = Join-Path $PSScriptRoot 'check-release.ps1'
foreach ($path in @($checker, (Join-Path $PSScriptRoot 'publish-release.ps1'))) {
    $tokens = $null
    $errors = $null
    [System.Management.Automation.Language.Parser]::ParseFile($path, [ref]$tokens, [ref]$errors) | Out-Null
    if (@($errors).Count -gt 0) { throw "PowerShell syntax error in $path." }
}

$folder = Join-Path ([IO.Path]::GetTempPath()) ("berwick-release-check-" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $folder | Out-Null
try {
    $zipPath = Join-Path $folder 'Berwick.Workshop.0.119.0.zip'
    Add-Type -AssemblyName System.IO.Compression
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [IO.Compression.ZipFile]::Open($zipPath, [IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($name in @('Berwick Workshop.exe', 'README.txt', 'Licenses.txt')) {
            $entry = $archive.CreateEntry($name)
            $writer = [IO.StreamWriter]::new($entry.Open())
            try { $writer.Write('test fixture') } finally { $writer.Dispose() }
        }
    }
    finally { $archive.Dispose() }

    $sha = (& git -C $root rev-parse HEAD).Trim()
    if ($LASTEXITCODE -ne 0) { throw 'Missing Git revision for test.' }
    $now = [DateTimeOffset]::UtcNow.ToString('o')
    $sample = @{
        version = '0.119.0'
        sourceCommit = $sha
        packageSha256 = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
        japanese = @{ imageSha256 = 'a' * 64; tests = 'passed'; checkedAtUtc = $now }
        english = @{ imageSha256 = 'b' * 64; tests = 'passed'; checkedAtUtc = $now }
        pcsx2 = @{
            version = 'synthetic test'
            japaneseModifiedBoot = 'passed'
            englishModifiedBoot = 'passed'
            smokeTestNotes = 'Synthetic data for checker regression testing only'
            checkedAtUtc = $now
        }
        packageContentsReviewed = $true
        licenseCreditsReviewed = $true
        releaseNotesReviewed = $true
    }
    $recordPath = Join-Path $folder 'test-evidence.json'
    function Write-Record { $sample | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $recordPath -Encoding UTF8 }
    function Expect-Rejected {
        $rejected = $false
        try { & $checker -Version '0.119.0' -Package $zipPath -Evidence $recordPath | Out-Null }
        catch { $rejected = $true }
        if (-not $rejected) { throw 'Invalid release evidence was accepted.' }
    }

    Write-Record
    & $checker -Version '0.119.0' -Package $zipPath -Evidence $recordPath | Out-Null
    if (-not $?) { throw 'Complete synthetic release evidence was rejected.' }

    $sample.packageSha256 = '0' * 64
    Write-Record
    Expect-Rejected
    $sample.packageSha256 = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()

    $sample.pcsx2.englishModifiedBoot = 'missing'
    Write-Record
    Expect-Rejected
    $sample.pcsx2.englishModifiedBoot = 'passed'

    $sample.japanese.tests = 'missing'
    Write-Record
    Expect-Rejected

    Write-Host 'Release evidence checker: synthetic acceptance and rejection tests passed.'
}
finally {
    Remove-Item -LiteralPath $folder -Recurse -Force -ErrorAction SilentlyContinue
}
