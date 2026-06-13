<#
.SYNOPSIS
    Run the reviewer against active Azure DevOps pull requests that still need your vote.

.DESCRIPTION
    Uses the Azure CLI azure-devops extension (`az repos pr list`) to discover
    active pull requests for a predefined project list and target branch list.

    The script skips draft PRs and invokes run.ps1 once per matching pull request.

.PARAMETER Organization
    Azure DevOps organization URL. Defaults to the static script value below.
    Example: https://dev.azure.com/MyOrganizationName/

.PARAMETER Projects
    Azure DevOps project names to scan. Defaults to the predefined list below.

.PARAMETER TargetBranches
    Target branch names to scan. Defaults to main, master, dev, develop.

.PARAMETER AdoToken
    Access token for Azure DevOps, passed to run.ps1. If omitted,
    run.ps1/common.psm1 gets one via Azure CLI.

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
    ./run-open-prs.ps1

.EXAMPLE
    ./run-open-prs.ps1 -Organization https://dev.azure.com/contoso/ -Projects Laekker.Kitchen -TargetBranches main,develop -MaxPullRequests 5 -DryRun
#>
[CmdletBinding()]
param(
    [string]   $Organization = "https://dev.azure.com/aveato/",
    [string[]] $Projects = @("Laekker.Kitchen", "AveatoApp", "Aveato", "Laekkerai.CustomerProjects"),
    [string[]] $TargetBranches = @("main", "master", "dev", "develop"),
    [string]   $AdoToken,
    [string]   $OpenAiApiKey = $env:OPENAI_API_KEY,
    [string]   $Language     = "German",
    [ValidateSet("none","nit","minor","major","blocker")]
    [string]   $FailOn       = "none",
    [ValidateSet("none","nit","minor","major","blocker")]
    [string]   $VoteWaitingOn = "major",
    [string]   $PiModel      = "openai/gpt-5.5",
    [string]   $Image        = "pr-review-bot:latest",
    [int]      $MaxPullRequests = 0,
    [switch]   $DryRun
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

Import-Module (Join-Path $PSScriptRoot 'common.psm1') -Force

function Normalize-OrganizationUrl {
    param([Parameter(Mandatory)][string]$Value)

    $normalized = $Value.Trim()
    if (-not $normalized) { Fail "Azure DevOps organization URL is required." }
    if ($normalized -notmatch '^https://') {
        Fail "Azure DevOps organization must be a URL, for example: https://dev.azure.com/MyOrganizationName/"
    }
    return $normalized.TrimEnd('/') + '/'
}

function Get-OrganizationNameFromUrl {
    param([Parameter(Mandatory)][string]$OrganizationUrl)

    if ($OrganizationUrl -match '^https://dev\.azure\.com/([^/]+)/?') { return $Matches[1] }
    if ($OrganizationUrl -match '^https://([^/.]+)\.visualstudio\.com/?') { return $Matches[1] }

    Fail "Could not derive organization short name from '$OrganizationUrl'. Expected https://dev.azure.com/{organization}/."
}

function Invoke-AzJson {
    param(
        [Parameter(Mandatory)][string[]]$Arguments,
        [Parameter(Mandatory)][string]$Context
    )

    $output = & az @Arguments --only-show-errors -o json 2>&1
    $exitCode = $LASTEXITCODE
    if ($exitCode -ne 0) {
        Fail "Azure CLI failed while ${Context}: $output"
    }

    $text = ($output | Out-String).Trim()
    if (-not $text) { return $null }

    try {
        return $text | ConvertFrom-Json
    } catch {
        Fail "Azure CLI returned invalid JSON while ${Context}: $_`n$text"
    }
}

function Normalize-RefName {
    param([string]$Branch)

    if (-not $Branch) { return $Branch }
    return $Branch -replace '^refs/heads/', ''
}

function Get-ObjectProperty {
    param(
        $InputObject,
        [Parameter(Mandatory)][string]$Name
    )

    if ($null -eq $InputObject) { return $null }
    $property = $InputObject.PSObject.Properties[$Name]
    if (-not $property) { return $null }
    return $property.Value
}

function Get-AzPullRequestsForProjectAndBranch {
    param(
        [Parameter(Mandatory)][string]$OrganizationUrl,
        [Parameter(Mandatory)][string]$Project,
        [Parameter(Mandatory)][string]$TargetBranch
    )

    $top = 100
    $skip = 0
    $allPullRequests = @()

    do {
        $page = Invoke-AzJson `
            -Arguments @(
                'repos', 'pr', 'list',
                '--organization', $OrganizationUrl,
                '--project', $Project,
                '--status', 'active',
                '--target-branch', $TargetBranch,
                '--top', ([string]$top),
                '--skip', ([string]$skip)
            ) `
            -Context "listing active PRs for project '$Project' targeting '$TargetBranch'"

        $items = @($page)
        $allPullRequests += $items
        $skip += $items.Count
    } while ($items.Count -eq $top)

    return $allPullRequests
}

if (-not (Get-Command az -ErrorAction SilentlyContinue)) {
    Fail "Azure CLI ('az') was not found on PATH. Install Azure CLI and the azure-devops extension."
}

if ($Organization -like '*CHANGE_ME*') {
    Fail "Set the default -Organization value in run-open-prs.ps1 or pass -Organization https://dev.azure.com/{organization}/."
}

if (-not $OpenAiApiKey) {
    Fail 'No model key. Set $env:OPENAI_API_KEY or pass -OpenAiApiKey.'
}

$Organization = Normalize-OrganizationUrl -Value $Organization
$Org = Get-OrganizationNameFromUrl -OrganizationUrl $Organization
$Projects = @($Projects | Where-Object { $_ } | ForEach-Object { Normalize-AdoSegment -Value $_ -Name 'ADO project' } | Select-Object -Unique)
$TargetBranches = @($TargetBranches | Where-Object { $_ } | ForEach-Object { Normalize-RefName $_.Trim() } | Where-Object { $_ } | Select-Object -Unique)

if (-not $Projects) { Fail "At least one project is required." }
if (-not $TargetBranches) { Fail "At least one target branch is required." }

$extension = Invoke-AzJson -Arguments @('extension', 'show', '--name', 'azure-devops') -Context 'checking Azure CLI azure-devops extension'
if (-not $extension) {
    Fail "Azure CLI azure-devops extension is not installed. Run: az extension add --name azure-devops"
}

$Token = Get-AdoToken $AdoToken

$AllPullRequests = @(
    foreach ($project in $Projects) {
        foreach ($targetBranch in $TargetBranches) {
            Write-Step "Listing active PRs in '$project' targeting '$targetBranch'..."
            foreach ($pullRequest in @(Get-AzPullRequestsForProjectAndBranch `
                -OrganizationUrl $Organization `
                -Project $project `
                -TargetBranch $targetBranch)) {
                [pscustomobject]@{
                    PullRequest = $pullRequest
                    Project     = $project
                }
            }
        }
    }
)

$seen = @{}
$SelectedPullRequests = @(
    foreach ($pullRequestEntry in $AllPullRequests) {
        $pullRequest = $pullRequestEntry.PullRequest
        $repository = Get-ObjectProperty -InputObject $pullRequest -Name 'repository'
        $projectName = $pullRequestEntry.Project
        $repoId = @(
            (Get-ObjectProperty -InputObject $repository -Name 'id'),
            (Get-ObjectProperty -InputObject $repository -Name 'name')
        ) |
            Where-Object { $_ } |
            Select-Object -First 1
        $repoName = @(
            (Get-ObjectProperty -InputObject $repository -Name 'name'),
            $repoId
        ) |
            Where-Object { $_ } |
            Select-Object -First 1
        $prId = [int](Get-ObjectProperty -InputObject $pullRequest -Name 'pullRequestId')

        if (-not $projectName -or -not $repoId -or $prId -le 0) { continue }

        $dedupeKey = "$projectName|$repoId|$prId"
        if ($seen.ContainsKey($dedupeKey)) { continue }
        $seen[$dedupeKey] = $true

        $targetBranch = Normalize-RefName (Get-ObjectProperty -InputObject $pullRequest -Name 'targetRefName')
        if ($TargetBranches -notcontains $targetBranch) { continue }
        if ((Get-ObjectProperty -InputObject $pullRequest -Name 'isDraft') -eq $true) { continue }

        [pscustomobject]@{
            PullRequest = $pullRequest
            Project     = $projectName
            RepoId      = $repoId
            RepoName    = $repoName
            Target      = $targetBranch
        }
    }
) | Sort-Object Project, RepoName, Target, { Get-ObjectProperty -InputObject $_.PullRequest -Name 'pullRequestId' }

if ($MaxPullRequests -gt 0) {
    $SelectedPullRequests = @($SelectedPullRequests | Select-Object -First $MaxPullRequests)
}

if (-not $SelectedPullRequests) {
    Write-Step "No active pull requests found on target branches: $($TargetBranches -join ', ')."
    exit 0
}

Write-Step ("Found {0} active pull request(s)." -f $SelectedPullRequests.Count)

$runScript = Join-Path $PSScriptRoot 'run.ps1'
$failedRuns = 0

foreach ($entry in $SelectedPullRequests) {
    $pullRequest = $entry.PullRequest
    $prId = [int](Get-ObjectProperty -InputObject $pullRequest -Name 'pullRequestId')
    $projectName = $entry.Project
    $repoId = $entry.RepoId
    $repoName = $entry.RepoName
    $title = Get-ObjectProperty -InputObject $pullRequest -Name 'title'
    Write-Step ("Running reviewer for PR #{0}: {1}/{2} -> {3} - {4}{5}" -f $prId, $projectName, $repoName, $entry.Target, $title, $(if ($DryRun) { " [dry run]" } else { "" }))

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
        -ContainerName "pr-review-bot-pr-$prId" `
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
