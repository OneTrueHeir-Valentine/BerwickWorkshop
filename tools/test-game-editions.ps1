param(
    [Parameter(Mandatory = $true)]
    [string] $JapaneseIso,

    [Parameter(Mandatory = $true)]
    [string] $EnglishIso,

    [string] $ReduxIso
)

$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$tests = Join-Path $root 'Tests/bin/Release/net10.0/Berwick.Tests.dll'

foreach ($disc in @($JapaneseIso, $EnglishIso)) {
    if (-not (Test-Path -LiteralPath $disc -PathType Leaf)) {
        throw "Disc image not found: $disc"
    }
}
if ($ReduxIso -and -not (Test-Path -LiteralPath $ReduxIso -PathType Leaf)) {
    throw "Comparison image not found: $ReduxIso"
}
& dotnet build (Join-Path $root 'Tests') --configuration Release --nologo
if ($LASTEXITCODE -ne 0) { throw 'The test build failed.' }

$inputs = @($JapaneseIso, $EnglishIso)
if ($ReduxIso) { $inputs += $ReduxIso }
& dotnet $tests @inputs
if ($LASTEXITCODE -ne 0) { throw 'The game-edition test suite failed.' }
Write-Host 'Both game editions passed the available integration tests.'
