<#
.SYNOPSIS
    Run the PR review bot against an Azure DevOps pull request.

.DESCRIPTION
    Pass just a PR URL and the container resolves everything (org, project,
    repo, PR id, branches) internally via the ADO REST API.

    Alternatively, pass individual params (-Org, -Project, -RepoId, -PrId)
    and branches will still be auto-resolved unless overridden.

    Use -DryRun to review only (print findings JSON) without posting to the PR.

.PARAMETER PrUrl
    Full Azure DevOps pull-request URL. When provided, the container handles
    all resolution internally — no other identity params are needed.
    Supported formats:
      https://dev.azure.com/{org}/{project}/_git/{repo}/pullrequest/{id}
      https://{org}.visualstudio.com/{project}/_git/{repo}/pullrequest/{id}

.PARAMETER Org
    Azure DevOps organization SHORT name (e.g. "contoso"). Required if -PrUrl is
    not given; otherwise auto-detected from the URL.

.PARAMETER Project
    Azure DevOps project name. Required if -PrUrl is not given.

.PARAMETER RepoId
    Repository id or name. Required if -PrUrl is not given.

.PARAMETER PrId
    Pull request id. Required if -PrUrl is not given.

.PARAMETER SourceBranch
    Override the source branch. By default auto-resolved from the ADO REST API.

.PARAMETER TargetBranch
    Override the target branch. By default auto-resolved from the ADO REST API.

.PARAMETER Language
    Comment language passed to the container as REVIEW_LANGUAGE. Default: English.

.PARAMETER FailOn
    none | nit | minor | major | blocker. Default: none.

.PARAMETER VoteWaitingOn
    Vote "waiting for author" on the PR when findings meet this severity threshold.
    none | nit | minor | major | blocker. Default: major.

.PARAMETER AdoToken
    Bearer token for ADO. If omitted, the script gets one via
    `az account get-access-token` for the Azure DevOps resource.

.PARAMETER OpenAiApiKey
    Model provider key for Pi. Defaults to $env:OPENAI_API_KEY.

.PARAMETER PiModel
    Model pattern Pi should use. Default: openai/gpt-5.5.

.PARAMETER Image
    Docker/Podman image tag. Default: pr-review-bot:latest.

.PARAMETER DryRun
    Review only; print the findings JSON and do not post to the PR.

.EXAMPLE
    # Simplest invocation — just the PR URL (container resolves everything):
    ./scripts/run.ps1 -PrUrl "https://dev.azure.com/contoso/Payments/_git/payments-api/pullrequest/1423"

    # Dry run to iterate on prompts without posting:
    ./scripts/run.ps1 -PrUrl "https://dev.azure.com/contoso/Payments/_git/payments-api/pullrequest/1423" -DryRun

    # Individual params (branches still auto-resolved):
    ./scripts/run.ps1 -Org contoso -Project Payments -RepoId payments-api -PrId 1423

    # German review comments:
    ./scripts/run.ps1 -PrUrl "https://dev.azure.com/contoso/Payments/_git/payments-api/pullrequest/1423" -Language German
#>
[CmdletBinding()]
param(
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
    [string] $AdoToken,
    [string] $OpenAiApiKey = $env:OPENAI_API_KEY,
    [string] $PiModel      = "openai/gpt-5.5",
    [string] $Image        = "pr-review-bot:latest",
    [switch] $DryRun
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

Import-Module (Join-Path $PSScriptRoot 'common.psm1') -Force

$runLabel = $null

# --- Prerequisites ------------------------------------------------------------
if (-not $OpenAiApiKey) {
    Fail 'No model key. Set $env:OPENAI_API_KEY or pass -OpenAiApiKey.'
}

$Runtime = Get-ContainerRuntime
$Token = Get-AdoToken $AdoToken

# --- Build env file -----------------------------------------------------------
# When -PrUrl is given, let the container resolve everything.
# When individual params are given, resolve on the host and pass them explicitly.
if ($PrUrl -and -not $SourceBranch -and -not $TargetBranch) {
    # Simplified path: container handles URL parsing + branch resolution
    $envFile = Write-EnvFile @{
        PrUrl         = $PrUrl
        Language      = $Language
        FailOn        = $FailOn
        VoteWaitingOn = $VoteWaitingOn
        PiModel       = $PiModel
        Token         = $Token
        OpenAiApiKey  = $OpenAiApiKey
        DryRun        = $DryRun
    }
    $runLabel = "Running reviewer with PR_URL (container resolves identity){0} [runtime: {1}]" -f $(if($DryRun){" [dry run]"}else{""}), $Runtime
} else {
    # Legacy path: resolve on host, pass individual vars
    if ($PrUrl) {
        $parsed = Resolve-PrUrl $PrUrl
        Write-Step "Parsed PR URL: org=$($parsed.Org) project=$($parsed.Project) repo=$($parsed.RepoId) pr=$($parsed.PrId)"
        $Org     = if ($Org)        { $Org }        else { $parsed.Org }
        $Project = if ($Project)    { $Project }    else { $parsed.Project }
        $RepoId  = if ($RepoId)     { $RepoId }     else { $parsed.RepoId }
        $PrId    = if ($PrId -gt 0) { $PrId }       else { $parsed.PrId }
    }

    if (-not $Org)     { Fail "-Org is required (or use -PrUrl to auto-detect)." }
    if (-not $Project) { Fail "-Project is required (or use -PrUrl to auto-detect)." }
    if (-not $RepoId)  { Fail "-RepoId is required (or use -PrUrl to auto-detect)." }
    if ($PrId -le 0)   { Fail "-PrId is required (or use -PrUrl to auto-detect)." }

    $branches = Resolve-PrBranches -Org $Org -Project $Project -RepoId $RepoId -PrId $PrId -Token $Token -SourceBranch $SourceBranch -TargetBranch $TargetBranch
    $Source = Normalize-BranchName $branches.SourceBranch
    $Target = Normalize-BranchName $branches.TargetBranch

    $envFile = Write-EnvFile @{
        Org           = $Org
        Project       = $Project
        RepoId        = $RepoId
        PrId          = $PrId
        Source        = $Source
        Target        = $Target
        Language      = $Language
        FailOn        = $FailOn
        VoteWaitingOn = $VoteWaitingOn
        PiModel       = $PiModel
        Token         = $Token
        OpenAiApiKey  = $OpenAiApiKey
        DryRun        = $DryRun
    }
    $runLabel = "Running reviewer on PR #{0} ({1} -> {2}){3} [runtime: {4}]" -f $PrId,$Source,$Target, $(if($DryRun){" [dry run]"}else{""}), $Runtime
}

$dockerArgs = @("run","--rm","--env-file",$envFile,$Image)

Write-Step $runLabel
try {
    & $Runtime @dockerArgs
    $rc = $LASTEXITCODE
} finally {
    Remove-Item -LiteralPath $envFile -Force -ErrorAction SilentlyContinue
}
Write-Step "Container exited with code $rc"
exit $rc
