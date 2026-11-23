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
$publicMarkdown = @(
    'README.md', 'LICENSE.md', 'LICENSING.md', 'CONTRIBUTING.md',
    'CHANGELOG.md', 'SECURITY.md', 'CODE_OF_CONDUCT.md', 'NOTICE.md'
)
$gitFiles = & git -C $root ls-files
if ($LASTEXITCODE -ne 0) { throw 'Unable to inspect tracked files.' }
foreach ($file in $gitFiles) {
    $normalized = $file.Replace('\', '/')
    if ($normalized -match '^(docs|release-handoff)/' -or
        $normalized -match '\.(iso|chd|cso|bin|cue)$') {
        throw "Local working file or game image is tracked: $file"
    }
    if ($normalized -match '\.md$' -and $normalized -notin $publicMarkdown -and
        $normalized -notmatch '^\.github/(ISSUE_TEMPLATE/[^/]+\.md|PULL_REQUEST_TEMPLATE\.md)$') {
        throw "Markdown file is outside the reviewed public documentation locations: $file"
    }
}
foreach ($relative in @('docs/local-notes.md', 'release-handoff/release-test.evidence.json')) {
    $ignored = & git -C $root check-ignore -- $relative
    if ($LASTEXITCODE -ne 0 -or -not $ignored) {
        throw "Local working path must remain Git-ignored: $relative"
    }
}
Write-Host 'Source boundaries and public file checks passed.'
