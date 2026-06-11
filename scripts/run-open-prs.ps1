<#
.SYNOPSIS
    Run the reviewer against every active pull request where you are not waiting for author.

.DESCRIPTION
    Uses the Azure DevOps REST API to discover active pull requests in a repo,
    keeps only PRs where the currently authenticated user is a reviewer, skips
    PRs where that reviewer vote is "waiting for author" (-5), and then invokes
    scripts/run.ps1 once per remaining pull request.

.PARAMETER Org
    Azure DevOps organization short name (for example: contoso).

.PARAMETER Project
    Azure DevOps project name. Optional; when omitted, all visible projects are scanned.

.PARAMETER AdoToken
    Access token for Azure DevOps. If omitted, the script gets one via Azure CLI.

.PARAMETER OpenAiApiKey
    Model provider key for Pi. Defaults to $env:OPENAI_API_KEY.

.PARAMETER Language
    Comment language passed to each run. Default: English.

.PARAMETER FailOn
    none | nit | minor | major | blocker. Default: none.

.PARAMETER VoteWaitingOn
    Vote "waiting for author" on the PR when findings meet this severity threshold.
    none | nit | minor | major | blocker. Default: major.

.PARAMETER PiModel
    Model pattern Pi should use. Default: openai/gpt-5.5.

.PARAMETER Image
    Docker/Podman image tag. Default: pr-review-bot:latest.

.PARAMETER MaxPullRequests
    Optional cap on the number of matching PRs to review. Default: 0 (no cap).

.PARAMETER DryRun
    Review only; print findings JSON for each PR and do not post to Azure DevOps.

.EXAMPLE
    ./scripts/run-open-prs.ps1 -Org contoso -Project Payments

.EXAMPLE
    ./scripts/run-open-prs.ps1 -Org contoso -MaxPullRequests 5 -DryRun
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $Org,
    [string] $Project,
    [string] $AdoToken,
    [string] $OpenAiApiKey = $env:OPENAI_API_KEY,
    [string] $Language     = "English",
    [ValidateSet("none","nit","minor","major","blocker")]
    [string] $FailOn       = "none",
    [ValidateSet("none","nit","minor","major","blocker")]
    [string] $VoteWaitingOn = "major",
    [string] $PiModel      = "openai/gpt-5.5",
    [string] $Image        = "pr-review-bot:latest",
    [int]    $MaxPullRequests = 0,
    [switch] $DryRun
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

Import-Module (Join-Path $PSScriptRoot 'common.psm1') -Force

$Token = Get-AdoToken $AdoToken
$CurrentUser = Get-AdoCurrentUser -Org $Org -Token $Token
$Repositories = @(Get-AdoRepositories -Org $Org -Project $Project -Token $Token)

if (-not $Repositories) {
    $scope = if ($Project) { "project '$Project'" } else { "organization '$Org'" }
    Write-Step "No repositories found for $scope."
    exit 0
}

$AllPullRequests = @(
    foreach ($repository in $Repositories) {
        $repositoryProject = $repository.project.name
        $repositoryId = if ($repository.id) { $repository.id } else { $repository.name }
        if (-not $repositoryProject -or -not $repositoryId) { continue }

        @(Get-ActivePullRequests -Org $Org -Project $repositoryProject -RepoId $repositoryId -Token $Token)
    }
)

$SelectedPullRequests = @(
    foreach ($pullRequest in $AllPullRequests) {
        $reviewer = Find-PrReviewer -PullRequest $pullRequest -ReviewerId $CurrentUser.Id -ReviewerUniqueName $CurrentUser.UniqueName
        if (-not $reviewer) { continue }
        if ($reviewer.vote -eq -5) { continue }

        $pullRequestProject = @($pullRequest.repository.project.name, $pullRequest.project.name) |
            Where-Object { $_ } |
            Select-Object -First 1
        $pullRequestRepoId = @($pullRequest.repository.id, $pullRequest.repository.name) |
            Where-Object { $_ } |
            Select-Object -First 1
        $pullRequestRepoName = @($pullRequest.repository.name, $pullRequestRepoId) |
            Where-Object { $_ } |
            Select-Object -First 1

        [pscustomobject]@{
            PullRequest = $pullRequest
            Reviewer    = $reviewer
            Project     = $pullRequestProject
            RepoId      = $pullRequestRepoId
            RepoName    = $pullRequestRepoName
        }
    }
) | Sort-Object Project, RepoName, { $_.PullRequest.pullRequestId }

if ($MaxPullRequests -gt 0) {
    $SelectedPullRequests = @($SelectedPullRequests | Select-Object -First $MaxPullRequests)
}

if (-not $SelectedPullRequests) {
    Write-Step "No active pull requests found where $($CurrentUser.DisplayName) is a reviewer and not waiting for author."
    exit 0
}

if (-not $OpenAiApiKey) {
    Fail 'No model key. Set $env:OPENAI_API_KEY or pass -OpenAiApiKey.'
}

Write-Step ("Found {0} active pull request(s) for {1} that are not waiting for author." -f $SelectedPullRequests.Count, $CurrentUser.DisplayName)

$runScript = Join-Path $PSScriptRoot 'run.ps1'
$failedRuns = 0

foreach ($entry in $SelectedPullRequests) {
    $pullRequest = $entry.PullRequest
    $prId = [int]$pullRequest.pullRequestId
    $projectName = $entry.Project
    $repoId = $entry.RepoId
    $repoName = $entry.RepoName
    if (-not $projectName -or -not $repoId) {
        $failedRuns++
        Write-Host "ERROR: PR #$prId is missing repository/project information; skipping" -ForegroundColor Red
        continue
    }
    $vote = [int]$entry.Reviewer.vote
    Write-Step ("Running reviewer for PR #{0}: {1}/{2} - {3} [current vote: {4}]{5}" -f $prId, $projectName, $repoName, $pullRequest.title, $vote, $(if ($DryRun) { " [dry run]" } else { "" }))

    & $runScript `
        -Org $Org `
        -Project $projectName `
        -RepoId $repoId `
        -PrId $prId `
        -Language $Language `
        -FailOn $FailOn `
        -VoteWaitingOn $VoteWaitingOn `
        -AdoToken $Token `
        -OpenAiApiKey $OpenAiApiKey `
        -PiModel $PiModel `
        -Image $Image `
        -DryRun:$DryRun

    $rc = $LASTEXITCODE
    if ($rc -ne 0) {
        $failedRuns++
        Write-Host "ERROR: PR #$prId review run failed with exit code $rc" -ForegroundColor Red
    }
}

if ($failedRuns -gt 0) {
    Fail "$failedRuns pull request review run(s) failed."
}

Write-Step ("Completed {0} pull request review run(s)." -f $SelectedPullRequests.Count)
