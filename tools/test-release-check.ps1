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

$gplNotice = [IO.File]::ReadAllText((Join-Path $root 'LICENSE'))
$referenceNotice = [IO.File]::ReadAllText((Join-Path $root 'reference/MintX-LICENSE.txt'))
$iconNotice = [IO.File]::ReadAllText((Join-Path $root 'App/Tabler-LICENSE.txt'))
$xdeltaNotices = @('xdelta3-LICENSE.txt', 'liblzma-COPYING.0BSD.txt', 'BLAKE3-LICENSE_A2.txt') |
    ForEach-Object { [IO.File]::ReadAllText((Join-Path $root "third-party/xdelta3/$_")) }
$completeNotices = $gplNotice + "`n`n" + $referenceNotice + "`n`n" + $iconNotice + "`n`n" + ($xdeltaNotices -join "`n`n")

# The checker compares tools/xdelta3.exe with the pinned hash. The real program is not in the repository, so a stand-in file
# and a matching temporary pin are used: copy the repository's checker next to a stand-in third-party folder.
$standIn = [Text.Encoding]::UTF8.GetBytes('synthetic xdelta3 stand-in')
$standInSha = ([BitConverter]::ToString([Security.Cryptography.SHA256]::Create().ComputeHash($standIn))).Replace('-', '').ToLowerInvariant()

$folder = Join-Path ([IO.Path]::GetTempPath()) ("berwick-release-check-" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $folder | Out-Null
$pinFile = Join-Path $root 'third-party/xdelta3/xdelta3.exe.sha256'
$realPin = [IO.File]::ReadAllBytes($pinFile)
[IO.File]::WriteAllText($pinFile, $standInSha + "`n")
try {
    $zipPath = Join-Path $folder 'Berwick.Workshop.0.119.0.zip'
    Add-Type -AssemblyName System.IO.Compression
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [IO.Compression.ZipFile]::Open($zipPath, [IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($name in @('Berwick Workshop.exe', 'README.txt', 'Licenses.txt')) {
            $entry = $archive.CreateEntry($name)
            $writer = [IO.StreamWriter]::new($entry.Open())
            $entryText = if ($name -eq 'Licenses.txt') { $completeNotices } else { 'test fixture' }
            try { $writer.Write($entryText) } finally { $writer.Dispose() }
        }
        $toolEntry = $archive.CreateEntry('tools/xdelta3.exe')
        $toolWriter = $toolEntry.Open()
        try { $toolWriter.Write($standIn, 0, $standIn.Length) } finally { $toolWriter.Dispose() }
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

    function Set-LicenseNotices([string] $text) {
        $package = [IO.Compression.ZipFile]::Open($zipPath, [IO.Compression.ZipArchiveMode]::Update)
        try {
            $package.GetEntry('Licenses.txt').Delete()
            $entry = $package.CreateEntry('Licenses.txt')
            $writer = [IO.StreamWriter]::new($entry.Open())
            try { $writer.Write($text) } finally { $writer.Dispose() }
        }
        finally { $package.Dispose() }
        $sample.packageSha256 = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
        Write-Record
    }

    Set-LicenseNotices $referenceNotice
    Expect-Rejected
    Set-LicenseNotices $gplNotice
    Expect-Rejected
    Set-LicenseNotices ($gplNotice + "`n`n" + $referenceNotice)
    Expect-Rejected
    Set-LicenseNotices ($gplNotice + "`n`n" + $referenceNotice + "`n`n" + $iconNotice)
    Expect-Rejected
    Set-LicenseNotices $completeNotices
    & $checker -Version '0.119.0' -Package $zipPath -Evidence $recordPath | Out-Null
    if (-not $?) { throw 'The restored license notices were rejected.' }

    function Set-Tool([byte[]] $bytes) {
        $package = [IO.Compression.ZipFile]::Open($zipPath, [IO.Compression.ZipArchiveMode]::Update)
        try {
            $old = $package.GetEntry('tools/xdelta3.exe')
            if ($null -ne $old) { $old.Delete() }
            if ($null -ne $bytes) {
                $entry = $package.CreateEntry('tools/xdelta3.exe')
                $stream = $entry.Open()
                try { $stream.Write($bytes, 0, $bytes.Length) } finally { $stream.Dispose() }
            }
        }
        finally { $package.Dispose() }
        $sample.packageSha256 = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
        Write-Record
    }
    Set-Tool ([Text.Encoding]::UTF8.GetBytes('tampered xdelta3'))
    Expect-Rejected
    Set-Tool $null
    Expect-Rejected
    Set-Tool $standIn
    & $checker -Version '0.119.0' -Package $zipPath -Evidence $recordPath | Out-Null
    if (-not $?) { throw 'The restored xdelta3 entry was rejected.' }

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
    [IO.File]::WriteAllBytes($pinFile, $realPin)
    Remove-Item -LiteralPath $folder -Recurse -Force -ErrorAction SilentlyContinue
}
