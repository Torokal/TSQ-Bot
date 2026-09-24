# Shared helpers for TSQ Bot scripts. Compatible with Windows PowerShell 5.1 and PowerShell 7+.
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$script:RepoRoot = Split-Path -Parent $PSScriptRoot
$script:BotProject = Join-Path $script:RepoRoot 'src\ToroSquad.Bot\ToroSquad.Bot.csproj'
$script:DataDir = Join-Path $script:RepoRoot 'data'

function Initialize-ToroEnvironment {
    # Make a freshly installed SDK visible in this session and keep CLI output predictable.
    $machine = [System.Environment]::GetEnvironmentVariable('Path', 'Machine')
    $user = [System.Environment]::GetEnvironmentVariable('Path', 'User')
    $env:Path = "$machine;$user"
    $env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
    $env:DOTNET_NOLOGO = '1'
    $env:DOTNET_CLI_UI_LANGUAGE = 'en'
    # Local runtime data lives in <repo>\data (git-ignored) instead of the bin folder.
    if (-not $env:TOROSQUAD_Bot__DataDirectory) { $env:TOROSQUAD_Bot__DataDirectory = $script:DataDir }
}

function Assert-DotNetSdk {
    $dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
    if (-not $dotnet) {
        throw ".NET SDK not found. Install .NET 10 SDK (see docs/WINDOWS_SETUP.md), e.g.: winget install Microsoft.DotNet.SDK.10"
    }
    $required = (Get-Content (Join-Path $script:RepoRoot 'global.json') -Raw | ConvertFrom-Json).sdk.version
    $sdks = & dotnet --list-sdks
    $major = $required.Split('.')[0]
    if (-not ($sdks | Where-Object { $_ -like "$major.*" })) {
        throw ".NET SDK $required (or a later $major.x patch) is required. Installed: $($sdks -join ', ')"
    }
    return $required
}

function Invoke-Bot {
    param([Parameter(ValueFromRemainingArguments = $true)][string[]] $BotArgs)
    # Pipe to the host so program output is shown, not returned as the function result.
    & dotnet run --project $script:BotProject --no-launch-profile -- @BotArgs | Out-Host
    return $LASTEXITCODE
}
