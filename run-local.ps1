<#
.SYNOPSIS
    Build and run the PR review bot locally against an Azure DevOps pull request.

.DESCRIPTION
    Convenience wrapper that combines build + run. For finer control use the
    split scripts directly:
      ./build.ps1              # build the image
      ./run.ps1 -PrUrl ...     # run against a PR

    All parameters are forwarded to run.ps1. Use -SkipBuild to skip
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
    # --- Run params (forwarded to run.ps1) ---
    [string] $PrUrl,
    [string] $Org,
    [string] $Project,
    [string] $RepoId,
    [int]    $PrId,
    [string] $SourceBranch,
    [string] $TargetBranch,
    [string] $Language,
    [ValidateSet("none","nit","minor","major","blocker")]
    [string] $FailOn,
    [ValidateSet("none","nit","minor","major","blocker")]
    [string] $VoteWaitingOn,
    [string] $AdoToken = $env:ADO_API_KEY,
    [string] $OpenAiApiKey = $env:OPENAI_API_KEY,
    [string] $PiModel,
    [string] $Image,
    [switch] $DryRun,

    # --- Build params ---
    [switch] $SkipBuild,
    [string] $PiVersion,
    [string] $EnvFile = ".env"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$ScriptsDir = $PSScriptRoot
Import-Module (Join-Path $ScriptsDir 'common.psm1') -Force
if ($EnvFile -and (Test-Path -LiteralPath $EnvFile)) { Import-DotEnv -Path $EnvFile }

# --- Build (unless skipped) ---------------------------------------------------
if (-not $SkipBuild) {
    $buildArgs = @{ EnvFile = $EnvFile }
    if ($Image) { $buildArgs.Image = $Image }
    if ($PiVersion) { $buildArgs.PiVersion = $PiVersion }
    & (Join-Path $ScriptsDir 'build.ps1') @buildArgs
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}

# --- Run ----------------------------------------------------------------------
# Forward all params. Empty strings / zero ints are the same as not passing
# them, so run.ps1 handles validation correctly.
$runScript = Join-Path $ScriptsDir 'run.ps1'

$runArgs = @{ EnvFile = $EnvFile }
if ($PrUrl) { $runArgs.PrUrl = $PrUrl }
if ($Org) { $runArgs.Org = $Org }
if ($Project) { $runArgs.Project = $Project }
if ($RepoId) { $runArgs.RepoId = $RepoId }
if ($PrId -gt 0) { $runArgs.PrId = $PrId }
if ($SourceBranch) { $runArgs.SourceBranch = $SourceBranch }
if ($TargetBranch) { $runArgs.TargetBranch = $TargetBranch }
if ($Language) { $runArgs.Language = $Language }
if ($FailOn) { $runArgs.FailOn = $FailOn }
if ($VoteWaitingOn) { $runArgs.VoteWaitingOn = $VoteWaitingOn }
if ($AdoToken) { $runArgs.AdoToken = $AdoToken }
if ($OpenAiApiKey) { $runArgs.OpenAiApiKey = $OpenAiApiKey }
if ($PiModel) { $runArgs.PiModel = $PiModel }
if ($Image) { $runArgs.Image = $Image }
if ($DryRun) { $runArgs.DryRun = $true }
& $runScript @runArgs
exit $LASTEXITCODE
