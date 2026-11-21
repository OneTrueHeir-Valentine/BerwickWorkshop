Set-StrictMode -Version Latest

function Assert-EvidencePath {
    param([string] $Path)

    $current = [IO.Path]::GetFullPath($Path)
    while ($current) {
        if (Test-Path -LiteralPath $current) {
            $item = Get-Item -LiteralPath $current -Force -ErrorAction Stop
            if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) {
                throw "Reparse point is not allowed in an evidence path: $current"
            }
        }
        $current = [IO.Path]::GetDirectoryName($current)
    }
}

function Get-EvidenceInput {
    param([string] $Path, [string] $Kind)

    if ([string]::IsNullOrWhiteSpace($Path)) { throw 'Evidence file not found: empty path.' }
    $withoutDrive = $Path -replace '^[A-Za-z]:', ''
    if ($Path.StartsWith('\\') -or $withoutDrive.Contains(':')) {
        throw "Only local files without alternate streams are supported: $Path"
    }
    if ($Path.IndexOfAny([char[]]'*?') -ge 0) { throw "Evidence file not found; wildcards are not accepted: $Path" }
    try {
        $provider = $null
        $drive = $null
        $full = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($Path, [ref]$provider, [ref]$drive)
    }
    catch { throw "Invalid evidence file path: $Path. $($_.Exception.Message)" }
    if ($provider.Name -ne 'FileSystem' -or $full.StartsWith('\\')) { throw "Only local files are supported: $Path" }
    if ($full -match '(?i)(^|[\\/])(bios|memcards?|memory[-_ ]?cards?|sstates|savestates|extracted([-_ ]assets)?|berwick-decomp|\.ssh|\.aws|\.gnupg|\.codex)([\\/]|$)' -or
        [IO.Path]::GetFileName($full) -match '(?i)^(credentials|secrets?|id_rsa|id_ed25519)(\.|$)') {
        throw "Private or game-data path is not allowed: $Path"
    }
    Assert-EvidencePath $full
    try { $item = Get-Item -LiteralPath $full -Force -ErrorAction Stop }
    catch { throw "Evidence file not found or inaccessible: $Path. $($_.Exception.Message)" }
    if ($item -isnot [IO.FileInfo]) { throw "Select a regular file, not a directory: $Path" }
    $allowed = if ($Kind -eq 'screenshot') { @('.png', '.jpg', '.jpeg', '.bmp') } else { @('.log', '.txt', '.json') }
    if ($allowed -notcontains $item.Extension) { throw "Unsupported $Kind extension: $Path" }
    try {
        $stream = [IO.File]::Open($full, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
        $stream.Dispose()
    }
    catch { throw "Cannot read evidence file: $Path. $($_.Exception.Message)" }
    return [pscustomobject]@{ path = $full; name = $item.Name; kind = $Kind }
}

function Export-TestEvidence {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)] [string] $RepositoryRoot,
        [Parameter(Mandatory = $true)] [ValidatePattern('^0\.[0-9]+\.[0-9]+$')] [string] $Version,
        [string[]] $Log = @(),
        [string[]] $Screenshot = @(),
        [ValidateLength(0, 80)] [string] $Pcsx2Version = ''
    )

    $ErrorActionPreference = 'Stop'
    if ($Pcsx2Version -match '[\r\n]') { throw 'PCSX2 version must be a single line.' }
    $root = [IO.Path]::GetFullPath($RepositoryRoot)
    $relative = 'release-handoff/collections'
    & git -C $root check-ignore --quiet -- $relative
    if ($LASTEXITCODE -ne 0) { throw 'Evidence output must be Git-ignored under release-handoff.' }
    $tracked = @(& git -C $root ls-files -- release-handoff)
    if ($LASTEXITCODE -ne 0 -or $tracked.Count -ne 0) { throw 'Evidence output must not contain tracked files.' }
    $commit = (& git -C $root rev-parse HEAD)
    if ($LASTEXITCODE -ne 0 -or $commit -notmatch '^[a-fA-F0-9]{40}$') { throw 'Could not determine the source commit.' }
    $status = @(& git -C $root status --porcelain)
    if ($LASTEXITCODE -ne 0) { throw 'Could not determine the working-tree status.' }

    $inputs = [System.Collections.Generic.List[object]]::new()
    $names = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($group in @(@{ kind = 'log'; paths = $Log }, @{ kind = 'screenshot'; paths = $Screenshot })) {
        foreach ($path in $group.paths) {
            $item = Get-EvidenceInput $path $group.kind
            if (-not $names.Add($item.name)) { throw "Duplicate filename in selected evidence: $($item.name)" }
            $inputs.Add($item)
        }
    }
    if ($inputs.Count -eq 0) { throw 'Select at least one log or screenshot file.' }

    $parent = Join-Path $root "$relative/$Version"
    Assert-EvidencePath $parent
    [IO.Directory]::CreateDirectory($parent) | Out-Null
    $id = [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss-fff') + '-' + [guid]::NewGuid().ToString('N')
    $destination = Join-Path $parent $id
    $staging = Join-Path $parent ('.collecting-' + $id)
    [IO.Directory]::CreateDirectory($staging) | Out-Null
    $ownedFiles = [System.Collections.Generic.List[string]]::new()
    $ownedFolders = [System.Collections.Generic.List[string]]::new()
    $ownedFolders.Add($staging)
    $files = [System.Collections.Generic.List[object]]::new()
    $collectedAt = [DateTimeOffset]::UtcNow.ToString('o')
    try {
        foreach ($item in $inputs) {
            $folderName = if ($item.kind -eq 'log') { 'logs' } else { 'screenshots' }
            $folder = Join-Path $staging $folderName
            if (-not [IO.Directory]::Exists($folder)) {
                [IO.Directory]::CreateDirectory($folder) | Out-Null
                $ownedFolders.Add($folder)
            }
            $copy = Join-Path $folder $item.name
            $source = $null
            $output = $null
            $hasher = $null
            try {
                Assert-EvidencePath $item.path
                # Deny concurrent writes while copying so metadata describes a stable snapshot.
                $source = [IO.File]::Open($item.path, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
                $output = [IO.File]::Open($copy, [IO.FileMode]::CreateNew, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
                $ownedFiles.Add($copy)
                $source.CopyTo($output)
                $output.Position = 0
                $hasher = [Security.Cryptography.SHA256]::Create()
                $hash = [BitConverter]::ToString($hasher.ComputeHash($output)).Replace('-', '').ToLowerInvariant()
                $files.Add([ordered]@{
                    kind = $item.kind
                    originalPath = $item.path
                    relativePath = "$folderName/$($item.name)"
                    sizeBytes = $source.Length
                    sha256 = $hash
                    collectedAtUtc = [DateTimeOffset]::UtcNow.ToString('o')
                })
            }
            catch { throw "Cannot collect evidence file: $($item.path). $($_.Exception.Message)" }
            finally {
                if ($hasher) { $hasher.Dispose() }
                if ($output) { $output.Dispose() }
                if ($source) { $source.Dispose() }
            }
        }
        $manifest = [ordered]@{
            schemaVersion = 1
            version = $Version
            sourceCommit = $commit.Trim().ToLowerInvariant()
            workingTreeClean = ($status.Count -eq 0)
            collectedAtUtc = $collectedAt
            pcsx2Version = if ($Pcsx2Version) { $Pcsx2Version } else { $null }
            files = @($files.ToArray())
        }
        $json = Join-Path $staging 'manifest.json'
        $ownedFiles.Add($json)
        [IO.File]::WriteAllText($json, ($manifest | ConvertTo-Json -Depth 5), [Text.UTF8Encoding]::new($false))
        $lines = @("Local test evidence, release $Version", "Collected: $collectedAt", "Source commit: $($manifest.sourceCommit)",
            "Working tree clean: $($manifest.workingTreeClean)", "PCSX2 version (supplied): $Pcsx2Version", '')
        foreach ($file in $files) {
            $lines += @("$($file.kind): $($file.originalPath)", "Copy: $($file.relativePath)",
                "Bytes: $($file.sizeBytes)", "SHA-256: $($file.sha256)", "Collected: $($file.collectedAtUtc)", '')
        }
        $text = Join-Path $staging 'manifest.txt'
        $ownedFiles.Add($text)
        [IO.File]::WriteAllLines($text, $lines, [Text.UTF8Encoding]::new($false))
        [IO.Directory]::Move($staging, $destination)
    }
    catch {
        foreach ($file in $ownedFiles.ToArray()) { [IO.File]::Delete($file) }
        for ($i = $ownedFolders.Count - 1; $i -ge 0; $i--) { [IO.Directory]::Delete($ownedFolders[$i], $false) }
        throw
    }
    return $destination
}

Export-ModuleMember -Function Export-TestEvidence
