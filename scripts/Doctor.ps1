<#
.SYNOPSIS
    Checks the local ToroSquad Bot environment and configuration. Never prints secret values.
.DESCRIPTION
    Verifies the .NET SDK, git, restores/builds the solution, then runs `ToroSquad.Bot doctor`, which reports
    token/API-key presence (set / not set), modes, database migrations, the slash-command manifest and provider
    status. BLOCKED lines are things that need your action (e.g. a Discord token) but do not stop local development.
.EXAMPLE
    .\scripts\Doctor.ps1
#>
[CmdletBinding()]
param()

. (Join-Path $PSScriptRoot '_Common.ps1')
Initialize-ToroEnvironment

Write-Host "== ToroSquad Bot doctor ==" -ForegroundColor Cyan
$sdk = Assert-DotNetSdk
Write-Host "[OK     ] .NET SDK $sdk family installed"
$git = Get-Command git -ErrorAction SilentlyContinue
if ($git) { Write-Host "[OK     ] git: $(& git --version)" } else { Write-Host "[WARN   ] git not found (needed for Export-Source.ps1)" -ForegroundColor Yellow }

$env:DOTNET_ENVIRONMENT = if ($env:DOTNET_ENVIRONMENT) { $env:DOTNET_ENVIRONMENT } else { 'Development' }
Push-Location $script:RepoRoot
try {
    & dotnet build (Join-Path $script:RepoRoot 'ToroSquad.slnx') -v q -nologo | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "Build failed." }
    $code = Invoke-Bot doctor
    exit $code
}
finally {
    Pop-Location
}
