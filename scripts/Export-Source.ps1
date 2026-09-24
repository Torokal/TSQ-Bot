<#
.SYNOPSIS
    Creates the Corresponding Source archive of the committed version (AGPL-3.0 section 13) under artifacts\.
.DESCRIPTION
    Uses `git archive` of HEAD, so only committed, tracked files are included: no local data, databases, logs,
    user-secrets or environment variables. The archive contains build instructions (README.md, docs/WINDOWS_SETUP.md).
    Host the archive (or the public repository at this commit) and set Bot:SourceUrl to it so /bot source can point
    users to the exact source of the running version. Refuses if there are uncommitted changes, because the running
    build would then differ from the archive.
.EXAMPLE
    .\scripts\Export-Source.ps1
#>
[CmdletBinding()]
param(
    [switch] $AllowDirty
)

. (Join-Path $PSScriptRoot '_Common.ps1')
Push-Location $script:RepoRoot
try {
    $status = & git status --porcelain
    if ($status -and -not $AllowDirty) {
        throw "Working tree has uncommitted changes. Commit first so the archive equals the running version (or use -AllowDirty for a draft)."
    }
    $sha = (& git rev-parse HEAD).Trim()
    $out = Join-Path $script:RepoRoot 'artifacts'
    New-Item -ItemType Directory -Force $out | Out-Null
    $file = Join-Path $out "tsq-bot-source-$($sha.Substring(0, 12)).zip"
    & git archive --format=zip --prefix "tsq-bot-$($sha.Substring(0, 12))/" -o $file HEAD
    if ($LASTEXITCODE -ne 0) { throw "git archive failed." }
    Write-Host "Corresponding Source: $file (commit $sha)" -ForegroundColor Green
    Write-Host "Publish it and set Bot:SourceUrl accordingly."
}
finally {
    Pop-Location
}
