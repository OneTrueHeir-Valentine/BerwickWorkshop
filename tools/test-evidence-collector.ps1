$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
Import-Module (Join-Path $PSScriptRoot 'EvidenceCollector.psm1') -Force
$fixture = Join-Path $root ('release-handoff/collector-tests/' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $fixture | Out-Null
$outputs = [System.Collections.Generic.List[string]]::new()
$passed = 0
$failed = 0

function Test([string] $Name, [scriptblock] $Body) {
    try { & $Body; $script:passed++; Write-Host "PASS $Name" }
    catch { $script:failed++; Write-Host "FAIL ${Name}: $($_.Exception.Message)" }
}
function Assert([bool] $Condition, [string] $Message) {
    if (-not $Condition) { throw $Message }
}
function Reject([scriptblock] $Body, [string] $Message) {
    try { & $Body | Out-Null }
    catch {
        Assert ($_.Exception.Message -like "*$Message*") "Unexpected error: $($_.Exception.Message)"
        return
    }
    throw 'Expected rejection'
}
function Collect([string[]] $Logs, [string[]] $Screenshots = @(), [string] $Emulator = '') {
    $directory = Export-TestEvidence -RepositoryRoot $root -Version '0.119.0' -Log $Logs -Screenshot $Screenshots -Pcsx2Version $Emulator
    $outputs.Add($directory)
    return $directory
}
function Write-Fixture([string] $Name, [byte[]] $Bytes) {
    $path = Join-Path $fixture $Name
    [IO.File]::WriteAllBytes($path, $Bytes)
    return $path
}

try {
    $log = Write-Fixture 'selected [1].log' ([Text.Encoding]::UTF8.GetBytes('abc'))
    $image = Write-Fixture 'screen.png' ([byte[]](137, 80, 78, 71, 13, 10, 26, 10, 0, 1, 255))
    $unselected = Write-Fixture 'unselected.txt' ([byte[]](9, 8, 7))
    $beforeTime = (Get-Item -LiteralPath $log).LastWriteTimeUtc

    Test 'Only selected files are copied with exact bytes and source metadata' {
        $directory = Collect @($log) @($image) '2.9.183'
        $manifest = Get-Content -LiteralPath (Join-Path $directory 'manifest.json') -Raw -Encoding UTF8 | ConvertFrom-Json
        Assert ($manifest.schemaVersion -eq 1 -and $manifest.version -eq '0.119.0') 'Wrong manifest version'
        Assert ($manifest.sourceCommit -eq (& git -C $root rev-parse HEAD).Trim()) 'Wrong source commit'
        Assert ($manifest.pcsx2Version -eq '2.9.183') 'Missing supplied PCSX2 version'
        $collectionTime = [DateTimeOffset]::MinValue
        Assert ([DateTimeOffset]::TryParse($manifest.collectedAtUtc, [ref]$collectionTime)) 'Invalid collection timestamp'
        Assert ($manifest.files.Count -eq 2) 'Unselected evidence collected'
        Assert ($directory.StartsWith((Join-Path $root 'release-handoff/collections/0.119.0'), [StringComparison]::OrdinalIgnoreCase)) 'Output outside release-handoff'
        foreach ($file in $manifest.files) {
            $copy = Join-Path $directory $file.relativePath
            Assert ([IO.File]::ReadAllBytes($copy).Length -eq $file.sizeBytes) 'Size mismatch'
            Assert ((Get-FileHash -LiteralPath $copy -Algorithm SHA256).Hash.ToLowerInvariant() -eq $file.sha256) 'Copied hash mismatch'
            Assert ((Get-FileHash -LiteralPath $file.originalPath -Algorithm SHA256).Hash.ToLowerInvariant() -eq $file.sha256) 'Source hash mismatch'
            Assert ([Convert]::ToBase64String([IO.File]::ReadAllBytes($copy)) -eq [Convert]::ToBase64String([IO.File]::ReadAllBytes($file.originalPath))) 'File contents changed'
            $time = [DateTimeOffset]::MinValue
            Assert ([DateTimeOffset]::TryParse($file.collectedAtUtc, [ref]$time)) 'Invalid file timestamp'
        }
        $logEntry = @($manifest.files | Where-Object { $_.kind -eq 'log' })[0]
        Assert ($logEntry.sha256 -eq 'ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad') 'Known SHA-256 vector failed'
        Assert ((Get-Item -LiteralPath $log).LastWriteTimeUtc -eq $beforeTime) 'Original log timestamp changed'
        Assert ((Get-Content -LiteralPath (Join-Path $directory 'manifest.txt') -Raw).Contains('selected [1].log')) 'Readable manifest missing source name'
        Assert ($null -eq $manifest.japanese -and $null -eq $manifest.english) 'Release verification statuses created'
        Assert ((Get-ChildItem -LiteralPath $directory -Recurse -File).Count -eq 4) 'Unexpected collected files'
    }
    Test 'Optional emulator version stays unspecified and collections never overwrite' {
        $first = Collect @($log)
        $second = Collect @($log)
        Assert ($first -ne $second) 'Collection directory reused'
        $manifest = Get-Content -LiteralPath (Join-Path $first 'manifest.json') -Raw -Encoding UTF8 | ConvertFrom-Json
        Assert ([string]::IsNullOrEmpty($manifest.pcsx2Version)) 'Emulator version guessed'
        Assert (Test-Path -LiteralPath (Join-Path $first 'logs/selected [1].log')) 'Original collection damaged'
    }
    Test 'Duplicate basenames are rejected case-insensitively before collection' {
        $other = Join-Path $fixture 'other'
        New-Item -ItemType Directory -Path $other | Out-Null
        $duplicate = Join-Path $other 'SELECTED [1].LOG'
        [IO.File]::WriteAllText($duplicate, 'different')
        Reject { Collect @($log, $duplicate) } 'Duplicate filename'
        Reject { Collect @($log, $log) } 'Duplicate filename'
    }
    Test 'Unicode source names are retained in both manifests' {
        $name = 'r' + [char]0xE9 + 'sultat.log'
        $path = Write-Fixture $name ([byte[]](4, 5, 6))
        $directory = Collect @($path)
        $json = Get-Content -LiteralPath (Join-Path $directory 'manifest.json') -Raw -Encoding UTF8 | ConvertFrom-Json
        Assert ($json.files[0].originalPath -eq $path) 'Unicode original path changed'
        Assert (Test-Path -LiteralPath (Join-Path $directory $json.files[0].relativePath)) 'Unicode copy path changed'
        Assert ((Get-Content -LiteralPath (Join-Path $directory 'manifest.txt') -Raw -Encoding UTF8).Contains($name)) 'Unicode readable manifest changed'
    }
    Test 'Missing files and directories produce clear errors' {
        $parent = Join-Path $root 'release-handoff/collections/0.119.0'
        $before = @(Get-ChildItem -LiteralPath $parent -Directory).Count
        Reject { Collect @($log, (Join-Path $fixture 'missing.log')) } 'Evidence file not found'
        Reject { Collect @($fixture) } 'regular file'
        Assert (@(Get-ChildItem -LiteralPath $parent -Directory).Count -eq $before) 'Invalid selection left a partial collection'
    }
    Test 'Locked files fail without leaving a collection' {
        $parent = Join-Path $root 'release-handoff/collections/0.119.0'
        $before = @(Get-ChildItem -LiteralPath $parent -Directory).Count
        $locked = [IO.File]::Open($log, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::None)
        try { Reject { Collect @($log) } 'Cannot read evidence file' }
        finally { $locked.Dispose() }
        Assert (@(Get-ChildItem -LiteralPath $parent -Directory).Count -eq $before) 'Failed collection left output'
    }
    Test 'Unsafe extensions and mismatched categories are rejected' {
        foreach ($name in @('game.iso', 'firmware.bin', 'card.ps2', 'save.p2s', 'assets.ar', 'private.pem', 'bundle.zip')) {
            $path = Write-Fixture $name ([byte[]](1))
            Reject { Collect @($path) } 'Unsupported log extension'
        }
        Reject { Collect @() @($log) } 'Unsupported screenshot extension'
        Reject { Collect @() } 'at least one'
    }
    Test 'Known private and game-data directories are rejected despite allowed suffixes' {
        foreach ($name in @('bios', 'memcards', 'sstates', 'extracted', '.ssh', '.aws')) {
            $folder = Join-Path $fixture $name
            New-Item -ItemType Directory -Path $folder | Out-Null
            $path = Join-Path $folder 'private.log'
            [IO.File]::WriteAllText($path, 'synthetic private data')
            Reject { Collect @($path) } 'Private or game-data path'
        }
    }
    Test 'Invalid release versions cannot escape the output directory' {
        Reject { Export-TestEvidence -RepositoryRoot $root -Version '../escape' -Log @($log) } 'Version'
    }
    Test 'Network paths, alternate streams and wildcard expansion are refused' {
        Reject { Collect @('\\localhost\share\private.log') } 'Only local files'
        Reject { Collect @("$($log):private.log") } 'Only local files'
        Reject { Collect @((Join-Path $fixture '*.log')) } 'Evidence file not found'
    }
    Test 'Output must be ignored and rooted in the repository' {
        Reject { Export-TestEvidence -RepositoryRoot (Join-Path $root 'tools') -Version '0.119.0' -Log @($log) } 'Git-ignored'
    }
    Test 'Junction sources are refused' {
        $junction = Join-Path $fixture 'redirect'
        $target = Join-Path $fixture 'other'
        New-Item -ItemType Junction -Path $junction -Target $target | Out-Null
        try { Reject { Collect @((Join-Path $junction 'SELECTED [1].LOG')) } 'Reparse point' }
        finally { [IO.Directory]::Delete($junction) }
    }
    Test 'Output junctions cannot redirect collections' {
        $fakeRoot = Join-Path $fixture 'checkout'
        New-Item -ItemType Directory -Path $fakeRoot | Out-Null
        $junction = Join-Path $fakeRoot 'release-handoff'
        New-Item -ItemType Junction -Path $junction -Target (Join-Path $fixture 'other') | Out-Null
        try { Reject { Export-TestEvidence -RepositoryRoot $fakeRoot -Version '0.119.0' -Log @($log) } 'Reparse point' }
        finally { [IO.Directory]::Delete($junction) }
    }
    Test 'CLI resolves its repository independently of the current directory' {
        Push-Location -LiteralPath $fixture
        try {
            $directory = & (Join-Path $root 'tools/collect-test-evidence.ps1') -Version '0.119.0' -Log @('./selected [1].log')
            $outputs.Add($directory)
            Assert (Test-Path -LiteralPath (Join-Path $directory 'manifest.json')) 'CLI did not create a collection'
            $manifest = Get-Content -LiteralPath (Join-Path $directory 'manifest.json') -Raw -Encoding UTF8 | ConvertFrom-Json
            Assert ($manifest.files[0].originalPath -eq $log) 'Relative input used the process directory instead of the PowerShell location'
        }
        finally { Pop-Location }
    }
}
finally {
    # Delete only this test's unique synthetic directories, never existing evidence.
    $allowed = [IO.Path]::GetFullPath((Join-Path $root 'release-handoff')) + [IO.Path]::DirectorySeparatorChar
    foreach ($path in @($outputs.ToArray()) + @($fixture)) {
        $full = [IO.Path]::GetFullPath($path)
        if (-not $full.StartsWith($allowed, [StringComparison]::OrdinalIgnoreCase)) { throw "Unsafe test cleanup: $full" }
        if (Test-Path -LiteralPath $full) { Remove-Item -LiteralPath $full -Recurse -Force }
    }
}
Write-Host "$passed passed, $failed failed"
if ($failed -ne 0) { throw 'Evidence collector tests failed.' }
