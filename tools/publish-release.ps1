param(
    [Parameter(Mandatory = $true)]
    [string] $Version,
    [Parameter(Mandatory = $true)]
    [string] $Package,
    [Parameter(Mandatory = $true)]
    [string] $Evidence,
    [Parameter(Mandatory = $true)]
    [string] $ReleaseNotes
)

$ErrorActionPreference = 'Stop'
$repo = 'OneTrueHeir-Valentine/BerwickWorkshop'
$source = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$packagePath = (Resolve-Path -LiteralPath $Package -ErrorAction Stop).Path
$notesPath = (Resolve-Path -LiteralPath $ReleaseNotes -ErrorAction Stop).Path
$checker = Join-Path $PSScriptRoot 'check-release.ps1'

& $checker -Version $Version -Package $packagePath -Evidence $Evidence
if (-not $?) { throw 'Release verification failed.' }

if (-not (Get-Command gh -ErrorAction SilentlyContinue)) {
    throw 'GitHub CLI is required to publish a release.'
}
$tag = "v$Version"
$head = (& git -C $source rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0) { throw 'Could not find the source commit.' }
$remote = (& gh repo view $repo --json nameWithOwner --jq '.nameWithOwner')
if ($LASTEXITCODE -ne 0 -or $remote.Trim() -ne $repo) {
    throw 'GitHub CLI could not verify the destination repository.'
}

$existing = & gh release list --repo $repo --json tagName --limit 100
if ($LASTEXITCODE -ne 0) { throw 'Could not query existing releases.' }
if (@($existing | ConvertFrom-Json | Where-Object { $_.tagName -eq $tag }).Count -gt 0) {
    throw "Release $tag already exists. Nothing was changed."
}

& gh release create $tag $packagePath --repo $repo --target $head --title "Berwick Workshop $Version" --notes-file $notesPath
if ($LASTEXITCODE -ne 0) { throw 'GitHub did not publish the release.' }
$details = & gh release view $tag --repo $repo --json assets | ConvertFrom-Json
if ($LASTEXITCODE -ne 0 -or
    -not @($details.assets | Where-Object { $_.name -eq [IO.Path]::GetFileName($packagePath) }).Count) {
    throw 'The release is present, but the expected application archive could not be verified.'
}
Write-Host "Published $tag from $head with the verified application archive."
