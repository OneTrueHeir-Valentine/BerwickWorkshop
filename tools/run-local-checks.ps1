param(
    [switch] $PublishApp
)

$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path

$revision = (& git -C $root rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0 -or $revision -notmatch '^[a-fA-F0-9]{40}$') {
    throw 'Could not determine the source revision.'
}

$dotnet = (Get-Command dotnet -ErrorAction Stop).Source
$shell = (Get-Process -Id $PID).Path
$runId = '{0}-{1}' -f [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss'),
    [guid]::NewGuid().ToString('N').Substring(0, 8)
$directory = Join-Path $root "release-handoff/checks/$runId"
New-Item -ItemType Directory -Path $directory -Force | Out-Null

$steps = [System.Collections.Generic.List[object]]::new()
$started = [DateTimeOffset]::UtcNow.ToString('o')
$clean = @(& git -C $root status --porcelain).Count -eq 0
$failure = $null

function Invoke-Check {
    param(
        [string] $Name,
        [string] $Executable,
        [string[]] $Arguments
    )

    $stdout = Join-Path $directory "$Name.stdout.log"
    $stderr = Join-Path $directory "$Name.stderr.log"
    $time = [DateTimeOffset]::UtcNow
    $exitCode = -1

    Write-Host "Running $Name..."
    try {
        Push-Location -LiteralPath $root
        try {
            # Native stderr must not abort the check before its exit code is read.
            $ErrorActionPreference = 'Continue'
            & $Executable @Arguments 1> $stdout 2> $stderr
            $exitCode = $LASTEXITCODE
            if ($null -eq $exitCode) {
                throw "The command did not report an exit code."
            }
        }
        finally {
            $ErrorActionPreference = 'Stop'
            Pop-Location
        }
    }
    catch {
        $_.Exception.Message | Set-Content -LiteralPath $stderr
    }

    $steps.Add([pscustomobject]@{
        name = $Name
        exitCode = $exitCode
        durationSeconds = [math]::Round(([DateTimeOffset]::UtcNow - $time).TotalSeconds, 2)
        stdout = [IO.Path]::GetFileName($stdout)
        stderr = [IO.Path]::GetFileName($stderr)
    })

    if ($exitCode -ne 0) {
        Write-Host "$Name failed (exit code $exitCode)."
        if (Test-Path -LiteralPath $stderr) {
            Get-Content -LiteralPath $stderr -Tail 12 | ForEach-Object { Write-Host $_ }
        }
        if (Test-Path -LiteralPath $stdout) {
            Get-Content -LiteralPath $stdout -Tail 12 | ForEach-Object { Write-Host $_ }
        }
        throw "Check failed: $Name"
    }

    Write-Host "$Name passed."
    if ($Name -eq 'offline-tests') {
        Get-Content -LiteralPath $stdout -Tail 1 | ForEach-Object { Write-Host $_ }
    }
}

try {
    Invoke-Check 'source-structure' $shell @(
        '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', 'tools/verify-structure.ps1'
    )
    Invoke-Check 'release-checker' $shell @(
        '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', 'tools/test-release-check.ps1'
    )
    Invoke-Check 'test-build' $dotnet @(
        'build', 'Tests', '-c', 'Release', '--nologo'
    )
    Invoke-Check 'offline-tests' $dotnet @(
        'Tests/bin/Release/net9.0/Berwick.Tests.dll'
    )

    if ($PublishApp) {
        $output = "release-handoff/checks/$runId/app"
        Invoke-Check 'app-publish' $dotnet @(
            'publish', 'App', '-c', 'Release', '-r', 'win-x64',
            '--self-contained', 'true', '-p:PublishSingleFile=true',
            '-p:EnableCompressionInSingleFile=true',
            '-p:IncludeNativeLibrariesForSelfExtract=true', '-o', $output
        )
        if (-not (Test-Path -LiteralPath (Join-Path $directory 'app/Berwick Workshop.exe'))) {
            throw 'The Windows executable was not produced.'
        }
    }
}
catch {
    $failure = $_.Exception.Message
}
finally {
    $result = [ordered]@{
        sourceCommit = $revision.ToLowerInvariant()
        workingTreeClean = $clean
        startedAtUtc = $started
        finishedAtUtc = [DateTimeOffset]::UtcNow.ToString('o')
        passed = [string]::IsNullOrEmpty($failure)
        failure = $failure
        checks = @($steps.ToArray())
    }
    $summary = Join-Path $directory 'summary.json'
    $result | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $summary -Encoding utf8
    Write-Host "Logs and summary: $directory"
}

if ($failure) {
    throw $failure
}

Write-Host 'Local checks passed. Game-edition and PCSX2 verification were not run.'
