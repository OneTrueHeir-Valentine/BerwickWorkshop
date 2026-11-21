# Checks public files and the three legacy files that are being divided into smaller modules.
# This script does not modify source or build outputs.
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$limits = @{
    'App/MainWindow.xaml.cs' = 2700
    'Core/Rules.cs' = 1300
    'Tests/Program.cs' = 1900
}
foreach ($entry in $limits.GetEnumerator()) {
    $path = Join-Path $root $entry.Key
    if (-not (Test-Path -LiteralPath $path)) { throw "Missing source file: $($entry.Key)" }
    $lines = @(Get-Content -LiteralPath $path).Count
    if ($lines -gt $entry.Value) {
        throw "$($entry.Key) has $lines lines, above its $($entry.Value) line limit. Move the new functionality into a dedicated module."
    }
}
$required = @(
    'App/MainWindow.Animations.cs',
    'App/MapEditorView.xaml',
    'App/MapEditorView.xaml.cs',
    'App/MapEditorController.cs',
    'App/MainWindow.Maps.cs',
    'Core/Rules.Shields.cs',
    'Core/MipsAssembly.cs',
    'Tests/MipsAssemblyTests.cs',
    'Core/Mips/heap-original.s',
    'Core/Mips/heap-reserved.s'
)
foreach ($relative in $required) {
    if (-not (Test-Path -LiteralPath (Join-Path $root $relative))) {
        throw "Missing architecture module: $relative"
    }
}
$gitFiles = & git -C $root ls-files
if ($LASTEXITCODE -ne 0) { throw 'Unable to inspect tracked files.' }
foreach ($file in $gitFiles) {
    $normalized = $file.Replace('\', '/')
    if ($normalized -match '(^|/)(AGENTS\.md|CLAUDE\.md|HANDOFF\.md|SESSION_LOG\.md)$' -or
        $normalized -match '\.(iso|chd|cso|bin|cue)$') {
        throw "Private coordination file or game asset is tracked: $file"
    }
}

# Shell and assembly sources must keep Unix line endings in Windows checkouts.
foreach ($script in @(Get-ChildItem -LiteralPath (Join-Path $root 'tools') -Filter '*.sh' -File)) {
    if ([IO.File]::ReadAllText($script.FullName).Contains("`r`n")) {
        throw "Bash script has Windows CRLF line endings: $($script.Name)"
    }
}
$attribute = & git -C $root check-attr eol -- 'tools/compare-shield-assembly.sh'
if ($LASTEXITCODE -ne 0 -or $attribute -notmatch ': eol: lf$') {
    throw 'Bash scripts must be checked out with LF line endings.'
}

$ignored = & git -C $root check-ignore -- 'release-handoff/release-test.evidence.json'
if ($LASTEXITCODE -ne 0 -or -not $ignored) {
    throw 'Local release-handoff evidence must remain Git-ignored.'
}
Write-Host 'Source boundaries and public file checks passed.'
