<#
.SYNOPSIS
    Registers TSQ Bot slash commands - DRY-RUN by default.
.DESCRIPTION
    1. Builds the command manifest offline and validates it (refuses to continue if invalid/empty/partially loaded).
    2. Logs in with the bot token (user-secrets / TOROSQUAD_Discord__Token) and verifies the token belongs to
       Discord:ApplicationId.
    3. Diffs against the commands currently registered and prints the plan.
    Changes are only made with -Apply. Commands this tool did not create are never deleted; commands it created but
    that are no longer defined are only deleted with -Prune. The guild must be listed in Discord:CommandSyncGuildIds.
    Global registration additionally requires Discord:AllowGlobalCommandSync=true (separate approval gate).
.EXAMPLE
    .\scripts\Sync-Commands.ps1 -GuildId 123456789012345678
.EXAMPLE
    .\scripts\Sync-Commands.ps1 -GuildId 123456789012345678 -Apply
#>
[CmdletBinding(DefaultParameterSetName = 'Guild')]
param(
    [Parameter(ParameterSetName = 'Guild', Mandatory = $true)] [string] $GuildId,
    [Parameter(ParameterSetName = 'Global', Mandatory = $true)] [switch] $Global,
    [switch] $Apply,
    [switch] $Prune
)

. (Join-Path $PSScriptRoot '_Common.ps1')
Initialize-ToroEnvironment
Assert-DotNetSdk | Out-Null
if (-not $env:DOTNET_ENVIRONMENT) { $env:DOTNET_ENVIRONMENT = 'Development' }

$botArgs = @('commands', 'sync')
if ($Global) { $botArgs += '--global' } else { $botArgs += @('--guild', $GuildId) }
if ($Apply) { $botArgs += '--apply' }
if ($Prune) { $botArgs += '--prune' }

Push-Location $script:RepoRoot
try {
    $code = Invoke-Bot @botArgs
    switch ($code) {
        0 { if (-not $Apply) { Write-Host "Dry-run complete. Nothing was changed on Discord." -ForegroundColor Green } }
        3 { Write-Host "BLOCKED - see messages above (token, application id or allow-list)." -ForegroundColor Yellow }
        default { Write-Host "Sync failed (exit $code)." -ForegroundColor Red }
    }
    exit $code
}
finally {
    Pop-Location
}
