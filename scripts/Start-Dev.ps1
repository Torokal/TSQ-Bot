<#
.SYNOPSIS
    Runs ToroSquad Bot locally.
.DESCRIPTION
    Default: Development environment with the SAFE defaults from appsettings.json — Fake Discord transport (no
    connection), fixture esports data, dry-run delivery. Nothing is sent anywhere.

    -Simulate runs a one-shot offline end-to-end demo (fixture data → planner → outbox → fake transport) against a
    temporary database and prints the resulting messages.

    Going live (real Discord gateway) is a configuration decision, not a script flag: set Discord:Transport=Gateway,
    the token via user-secrets and Bot:SourceUrl, then start normally. See docs/WINDOWS_SETUP.md.
.EXAMPLE
    .\scripts\Start-Dev.ps1
.EXAMPLE
    .\scripts\Start-Dev.ps1 -Simulate
#>
[CmdletBinding()]
param(
    [switch] $Simulate
)

. (Join-Path $PSScriptRoot '_Common.ps1')
Initialize-ToroEnvironment
Assert-DotNetSdk | Out-Null
if (-not $env:DOTNET_ENVIRONMENT) { $env:DOTNET_ENVIRONMENT = 'Development' }

Push-Location $script:RepoRoot
try {
    if ($Simulate) {
        exit (Invoke-Bot simulate)
    }
    Write-Host "Starting ToroSquad Bot ($env:DOTNET_ENVIRONMENT). Data: $env:TOROSQUAD_Bot__DataDirectory. Ctrl+C to stop." -ForegroundColor Cyan
    exit (Invoke-Bot run)
}
finally {
    Pop-Location
}
