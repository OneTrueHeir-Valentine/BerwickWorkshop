$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path

Push-Location $root
try {
    $tracked = @(& git ls-files)
    if ($LASTEXITCODE -ne 0) { throw 'Could not list tracked files.' }

    $files = @($tracked | Where-Object {
        $_ -in @('README.md', 'CONTRIBUTING.md', 'CHANGELOG.md', 'SECURITY.md') -or
        $_ -match '^\.github/.*\.(md|yml|yaml)$' -or
        $_ -match '^(App|Core)/.*\.(cs|xaml|resx)$'
    })

    $terms = @(
        '(?i)\b(?:MintX|MinN-11|Cyan-Alvein|EditorTK|RandomizerTK|Bermint|FEBuilder|Yune|Ignis)\b',
        '(?i)\b(?:colours?|behaviours?|defences?|armours?|catalogues?|organisations?|organis(?:e|ed|ing)|randomis(?:e|ed|ation)|optimis(?:e|ed|ation)|customis(?:e|ed|ation)|licences?|centres?|grey)\b'
    )
    $findings = [System.Collections.Generic.List[string]]::new()

    foreach ($file in $files) {
        $path = Join-Path $root $file.Replace('/', [IO.Path]::DirectorySeparatorChar)
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { continue }

        $lines = [IO.File]::ReadAllLines($path)
        for ($i = 0; $i -lt $lines.Length; $i++) {
            $text = $lines[$i]
            if ($file -match '\.cs$') {
                if ($text.TrimStart().StartsWith('//')) { continue }
                $strings = @([regex]::Matches($text, '"(?:\\.|[^"\\])*"') |
                    ForEach-Object { $_.Value })
                if ($strings.Count -eq 0) { continue }
                $text = [regex]::Replace(($strings -join ' '), '\{[^{}]*\}', '')
            }

            foreach ($term in $terms) {
                $matches = [regex]::Matches($text, $term)
                foreach ($match in $matches) {
                    $findings.Add(('{0}:{1}: {2}' -f $file, ($i + 1), $match.Value))
                }
            }
        }
    }

    if ($findings.Count -gt 0) {
        Write-Host 'Review public text at these locations:'
        $findings | Sort-Object -Unique | ForEach-Object { Write-Host "  $_" }
        exit 1
    }

    Write-Host "Public copy check passed ($($files.Count) files)."
}
finally {
    Pop-Location
}
