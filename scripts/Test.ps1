<#
.SYNOPSIS
    Builds with analyzers as errors, checks formatting, runs all tests (unit, contract, SQLite integration,
    recovery, architecture) and writes a TRX report to TestResults\.
.PARAMETER SkipFormat
    Skip `dotnet format --verify-no-changes` (it is slow on first run).
.PARAMETER Repeat
    Run the test suite N times to detect flaky/order-dependent tests.
.EXAMPLE
    .\scripts\Test.ps1
.EXAMPLE
    .\scripts\Test.ps1 -Repeat 3
#>
[CmdletBinding()]
param(
    [switch] $SkipFormat,
    [int] $Repeat = 1
)

. (Join-Path $PSScriptRoot '_Common.ps1')
Initialize-ToroEnvironment
Assert-DotNetSdk | Out-Null
$solution = Join-Path $script:RepoRoot 'ToroSquad.slnx'
$results = Join-Path $script:RepoRoot 'TestResults'

Push-Location $script:RepoRoot
try {
    Write-Host "== build (warnings are errors, analyzers on) ==" -ForegroundColor Cyan
    & dotnet build $solution -c Release -v q -nologo
    if ($LASTEXITCODE -ne 0) { throw "Build failed." }

    if (-not $SkipFormat) {
        Write-Host "== format check ==" -ForegroundColor Cyan
        & dotnet format $solution --verify-no-changes --no-restore -v q
        if ($LASTEXITCODE -ne 0) { throw "Formatting differs. Run: dotnet format ToroSquad.slnx" }
    }

    Write-Host "== manifest validation ==" -ForegroundColor Cyan
    & dotnet run --project $script:BotProject -c Release --no-build --no-launch-profile -- commands export
    if ($LASTEXITCODE -ne 0) { throw "Slash command manifest is invalid." }

    for ($i = 1; $i -le $Repeat; $i++) {
        Write-Host "== tests (run $i of $Repeat) ==" -ForegroundColor Cyan
        & dotnet test $solution -c Release --no-build --logger "trx;LogFileName=torosquad-tests-$i.trx" --results-directory $results
        if ($LASTEXITCODE -ne 0) { throw "Tests failed on run $i." }
    }
    Write-Host "All checks passed. Reports: $results" -ForegroundColor Green
}
finally {
    Pop-Location
}
