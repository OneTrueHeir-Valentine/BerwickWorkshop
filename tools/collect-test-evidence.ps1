[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)] [ValidatePattern('^0\.[0-9]+\.[0-9]+$')] [string] $Version,
    [string[]] $Log = @(),
    [string[]] $Screenshot = @(),
    [ValidateLength(0, 80)] [string] $Pcsx2Version = ''
)

$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'EvidenceCollector.psm1') -Force
Export-TestEvidence -RepositoryRoot (Join-Path $PSScriptRoot '..') -Version $Version `
    -Log $Log -Screenshot $Screenshot -Pcsx2Version $Pcsx2Version
