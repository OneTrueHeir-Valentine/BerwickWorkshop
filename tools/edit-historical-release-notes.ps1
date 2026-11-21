param(
    [switch] $Apply
)

$ErrorActionPreference = 'Stop'
$repo = 'OneTrueHeir-Valentine/BerwickWorkshop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$history = [IO.File]::ReadAllText((Join-Path $root 'docs/RELEASE_HISTORY.md'))
$sections = [regex]::Matches($history,
    '(?ms)^## Version (?<version>0\.\d+\.\d+)\r?\n(?<body>.*?)(?=^## Version |\z)')

$expected = @('0.118.0', '0.114.0')
if ($sections.Count -ne $expected.Count) {
    throw 'Release history does not contain exactly the expected version sections.'
}

if ($Apply -and -not (Get-Command gh -ErrorAction SilentlyContinue)) {
    throw 'GitHub CLI is required to update the release descriptions.'
}

foreach ($section in $sections) {
    $version = $section.Groups['version'].Value
    if ($expected -notcontains $version) {
        throw "Unexpected version in release history: $version."
    }
    $tag = "v$version"
    $notes = $section.Groups['body'].Value.Trim()
    if ([string]::IsNullOrWhiteSpace($notes)) {
        throw "Release notes for $tag are empty."
    }

    if (-not $Apply) {
        Write-Host "Preview for $tag"
        Write-Host $notes
        Write-Host ''
        continue
    }

    $release = & gh release view $tag --repo $repo --json tagName,assets | ConvertFrom-Json
    if ($LASTEXITCODE -ne 0 -or $null -eq $release -or $release.tagName -ne $tag) {
        throw "Could not verify the existing release $tag."
    }
    $expectedAsset = "Berwick.Workshop.$version.zip"
    if (@($release.assets | Where-Object { $_.name -eq $expectedAsset }).Count -ne 1) {
        throw "Release $tag is missing its expected application archive."
    }

    $temporary = [IO.Path]::GetTempFileName()
    try {
        [IO.File]::WriteAllText($temporary, $notes, [Text.UTF8Encoding]::new($false))
        & gh release edit $tag --repo $repo --notes-file $temporary
        if ($LASTEXITCODE -ne 0) {
            throw "Updating $tag failed."
        }
    }
    finally {
        Remove-Item -LiteralPath $temporary -ErrorAction SilentlyContinue
    }

    $updated = & gh release view $tag --repo $repo --json body | ConvertFrom-Json
    if ($LASTEXITCODE -ne 0 -or
        $updated.body.Replace([char]13, '').Trim() -ne $notes.Replace([char]13, '').Trim()) {
        throw "Could not verify the new description for $tag."
    }
    Write-Host "Updated the description for $tag; its tag and files were retained."
}

if (-not $Apply) {
    Write-Host 'Preview only. Run with -Apply to replace the two GitHub release descriptions.'
}
