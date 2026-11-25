# Checks that links in the public Markdown files point to files that are part of the repository.
# This script does not modify any files.
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path

Push-Location $root
try {
    $tracked = @(& git ls-files)
    if ($LASTEXITCODE -ne 0) { throw 'Could not list tracked files.' }
    $trackedSet = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($file in $tracked) { [void]$trackedSet.Add($file.Replace('\', '/')) }

    $documents = @($tracked | Where-Object { $_ -match '\.md$' })
    $findings = [System.Collections.Generic.List[string]]::new()

    foreach ($document in $documents) {
        $path = Join-Path $root $document.Replace('/', [IO.Path]::DirectorySeparatorChar)
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { continue }
        $folder = [IO.Path]::GetDirectoryName($document.Replace('\', '/'))
        $lines = [IO.File]::ReadAllLines($path)
        for ($i = 0; $i -lt $lines.Length; $i++) {
            foreach ($match in [regex]::Matches($lines[$i], '\]\(([^)\s]+)\)')) {
                $target = $match.Groups[1].Value
                if ($target -match '^(https?:|mailto:|#)') { continue }
                $target = ($target -split '#')[0]
                if ($target.Length -eq 0) { continue }
                $combined = if ($folder) { "$folder/$target" } else { $target }
                $resolved = [IO.Path]::GetFullPath((Join-Path $root $combined))
                $relative = $resolved.Substring($root.Length).TrimStart('\', '/').Replace('\', '/')
                if (-not $trackedSet.Contains($relative)) {
                    $findings.Add(('{0}:{1}: {2} is not a tracked file' -f $document, ($i + 1), $target))
                }
            }
        }
    }

    if ($findings.Count -gt 0) {
        Write-Host 'Fix these documentation links:'
        $findings | Sort-Object -Unique | ForEach-Object { Write-Host "  $_" }
        exit 1
    }

    Write-Host "Documentation links point to tracked files ($($documents.Count) files)."
}
finally {
    Pop-Location
}
