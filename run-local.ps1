<#
.SYNOPSIS
    Build and run the PR review bot locally against an Azure DevOps pull request.

.DESCRIPTION
    Convenience wrapper that combines build + run. For finer control use the
    split scripts directly:
      ./scripts/build.ps1              # build the image
      ./scripts/run.ps1 -PrUrl ...     # run against a PR

    All parameters are forwarded to scripts/run.ps1. Use -SkipBuild to skip
    the build step and reuse an existing image.

.EXAMPLE
    # Simplest: just the PR URL
    ./run-local.ps1 -PrUrl "https://dev.azure.com/contoso/Payments/_git/payments-api/pullrequest/1423"

    # Dry run with German comments, skip build
    ./run-local.ps1 -PrUrl "https://dev.azure.com/contoso/Payments/_git/payments-api/pullrequest/1423" -DryRun -Language German -SkipBuild

    # Legacy style (individual params)
    ./run-local.ps1 -Org contoso -Project Payments -RepoId payments-api -PrId 1423 -SkipBuild
#>
[CmdletBinding()]
param(
    # --- Run params (forwarded to scripts/run.ps1) ---
    [string] $PrUrl,
    [string] $Org,
    [string] $Project,
    [string] $RepoId,
    [int]    $PrId,
    [string] $SourceBranch,
    [string] $TargetBranch,
    [string] $Language     = "English",
    [ValidateSet("none","nit","minor","major","blocker")]
    [string] $FailOn       = "none",
    [ValidateSet("none","nit","minor","major","blocker")]
    [string] $VoteWaitingOn = "major",
    [string] $AdoToken = $env:ADO_API_KEY,
    [string] $OpenAiApiKey = $env:OPENAI_API_KEY,
    [string] $PiModel      = "openai/gpt-5.5",
    [string] $Image        = "pr-review-bot:latest",
    [switch] $DryRun,

    # --- Build params ---
    [switch] $SkipBuild,
    [string] $PiVersion     = "0.79.1",
    [string] $AdoMcpVersion = "2.7.0"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$ScriptsDir = Join-Path $PSScriptRoot 'scripts'

# --- Build (unless skipped) ---------------------------------------------------
if (-not $SkipBuild) {
    $buildArgs = @{
        Image         = $Image
        PiVersion     = $PiVersion
        AdoMcpVersion = $AdoMcpVersion
    }
    & (Join-Path $ScriptsDir 'build.ps1') @buildArgs
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}

# --- Run ----------------------------------------------------------------------
# Forward all params. Empty strings / zero ints are the same as not passing
# them, so run.ps1 handles validation correctly.
$runScript = Join-Path $ScriptsDir 'run.ps1'

& $runScript `
    -PrUrl $PrUrl `
    -Org $Org `
    -Project $Project `
    -RepoId $RepoId `
    -PrId $PrId `
    -SourceBranch $SourceBranch `
    -TargetBranch $TargetBranch `
    -Language $Language `
    -FailOn $FailOn `
    -VoteWaitingOn $VoteWaitingOn `
    -AdoToken $AdoToken `
    -OpenAiApiKey $OpenAiApiKey `
    -PiModel $PiModel `
    -Image $Image `
    -DryRun:$DryRun
exit $LASTEXITCODE
