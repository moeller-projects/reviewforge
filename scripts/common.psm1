# Shared functions for pr-review-bot PowerShell scripts.
# Import: Import-Module (Join-Path $PSScriptRoot 'common.psm1') -Force

# Well-known Azure DevOps AAD application id.
$script:AdoResource = "499b84ac-1321-427f-aa17-267ca6975798"

function Write-Step {
    param([string]$Message)
    Write-Host "==> $Message" -ForegroundColor Cyan
}

function Fail {
    param([string]$Message)
    Write-Host "ERROR: $Message" -ForegroundColor Red
    exit 1
}

<#
.SYNOPSIS
    Detect the container runtime (docker or podman).
#>
function Get-ContainerRuntime {
    if (Get-Command docker -ErrorAction SilentlyContinue) { return "docker" }
    if (Get-Command podman -ErrorAction SilentlyContinue) { return "podman" }
    Fail "Neither podman nor docker found on PATH."
}

<#
.SYNOPSIS
    Parse an ADO pull-request URL into its components.

.SupportedFormats
    https://dev.azure.com/{org}/{project}/_git/{repo}/pullrequest/{id}
    https://{org}.visualstudio.com/{project}/_git/{repo}/pullrequest/{id}
#>
function Resolve-PrUrl {
    param([Parameter(Mandatory)][string]$Url)

    # dev.azure.com format
    if ($Url -match 'dev\.azure\.com/([^/]+)/([^/]+)/_git/([^/]+)/pullrequest/(\d+)') {
        return @{ Org = $Matches[1]; Project = $Matches[2]; RepoId = $Matches[3]; PrId = [int]$Matches[4] }
    }
    # visualstudio.com format
    if ($Url -match '://([^/]+)\.visualstudio\.com/([^/]+)/_git/([^/]+)/pullrequest/(\d+)') {
        return @{ Org = $Matches[1]; Project = $Matches[2]; RepoId = $Matches[3]; PrId = [int]$Matches[4] }
    }

    Fail "Could not parse PR URL. Expected: https://dev.azure.com/{org}/{project}/_git/{repo}/pullrequest/{id}"
}

<#
.SYNOPSIS
    Acquire an ADO bearer token — either from the caller or via Azure CLI.
#>
function Get-AdoToken {
    param(
        [string]$AdoToken
    )

    if ($AdoToken) { return $AdoToken }

    if (-not (Get-Command az -ErrorAction SilentlyContinue)) {
        Fail 'No -AdoToken given and Azure CLI (az) not found. Install az and run "az login", or pass -AdoToken.'
    }
    Write-Step "Getting an Azure DevOps token via Azure CLI..."
    try {
        $token = (az account get-access-token --resource $script:AdoResource --query accessToken -o tsv 2>$null)
    } catch { $token = $null }
    if (-not $token) {
        Fail 'Could not get a token. Run "az login" (and "az account set --subscription <id>" for the right tenant) first.'
    }
    return $token
}

<#
.SYNOPSIS
    Invoke an Azure DevOps REST GET request with bearer auth.
#>
function Invoke-AdoGet {
    param(
        [Parameter(Mandatory)][string]$Uri,
        [Parameter(Mandatory)][string]$Token,
        [string]$Context = "Azure DevOps API"
    )

    try {
        return Invoke-RestMethod -Uri $Uri -Headers @{ Authorization = ('Bearer ' + $Token) } -ErrorAction Stop
    } catch {
        Fail "Failed calling ${Context}: $_"
    }
}

<#
.SYNOPSIS
    Resolve the current Azure DevOps user for the supplied org/token.
#>
function Get-AdoCurrentUser {
    param(
        [Parameter(Mandatory)][string]$Org,
        [Parameter(Mandatory)][string]$Token
    )

    Write-Step "Resolving current Azure DevOps user..."
    $encodedOrg = [System.Uri]::EscapeDataString($Org)
    $apiUrl = "https://dev.azure.com/$encodedOrg/_apis/connectionData?connectOptions=1&lastChangeId=-1&lastChangeId64=-1&api-version=7.1-preview.1"
    $data = Invoke-AdoGet -Uri $apiUrl -Token $Token -Context "Azure DevOps connection data"
    $user = $data.authenticatedUser

    if (-not $user -or -not $user.id) {
        Fail "ADO connection data did not return an authenticated user id."
    }

    $displayName = @($user.providerDisplayName, $user.customDisplayName, $user.displayName, $user.id) |
        Where-Object { $_ } |
        Select-Object -First 1

    return [pscustomobject]@{
        Id          = $user.id
        UniqueName  = $user.uniqueName
        DisplayName = $displayName
    }
}

<#
.SYNOPSIS
    List all active pull requests for a repository.
#>
function Get-ActivePullRequests {
    param(
        [Parameter(Mandatory)][string]$Org,
        [Parameter(Mandatory)][string]$Project,
        [Parameter(Mandatory)][string]$RepoId,
        [Parameter(Mandatory)][string]$Token
    )

    $top = 100
    $skip = 0
    $allPullRequests = @()

    do {
        $encodedOrg = [System.Uri]::EscapeDataString($Org)
        $encodedProject = [System.Uri]::EscapeDataString($Project)
        $encodedRepoId = [System.Uri]::EscapeDataString($RepoId)
        $apiUrl = "https://dev.azure.com/$encodedOrg/$encodedProject/_apis/git/repositories/$encodedRepoId/pullrequests?searchCriteria.status=active&`$top=$top&`$skip=$skip&api-version=7.0"
        $response = Invoke-AdoGet -Uri $apiUrl -Token $Token -Context "Azure DevOps pull request list"
        $page = @($response.value)
        $allPullRequests += $page
        $skip += $page.Count
    } while ($page.Count -eq $top)

    return $allPullRequests
}

<#
.SYNOPSIS
    Find a reviewer entry for the current user on a pull request.
#>
function Find-PrReviewer {
    param(
        [Parameter(Mandatory)]$PullRequest,
        [Parameter(Mandatory)][string]$ReviewerId,
        [string]$ReviewerUniqueName
    )

    $reviewers = @($PullRequest.reviewers)
    if (-not $reviewers) { return $null }

    $reviewer = $reviewers | Where-Object { $_.id -eq $ReviewerId } | Select-Object -First 1
    if ($reviewer) { return $reviewer }

    if ($ReviewerUniqueName) {
        $normalizedName = $ReviewerUniqueName.ToLowerInvariant()
        return $reviewers |
            Where-Object { $_.uniqueName -and $_.uniqueName.ToLowerInvariant() -eq $normalizedName } |
            Select-Object -First 1
    }

    return $null
}

<#
.SYNOPSIS
    Resolve source and target branches from the ADO REST API.
#>
function Resolve-PrBranches {
    param(
        [Parameter(Mandatory)][string]$Org,
        [Parameter(Mandatory)][string]$Project,
        [Parameter(Mandatory)][string]$RepoId,
        [Parameter(Mandatory)][int]$PrId,
        [Parameter(Mandatory)][string]$Token,
        [string]$SourceBranch,
        [string]$TargetBranch
    )

    # Only call the API if at least one branch is missing
    if ($SourceBranch -and $TargetBranch) { return @{ SourceBranch = $SourceBranch; TargetBranch = $TargetBranch } }

    Write-Step "Resolving PR branches from ADO REST API..."
    $apiUrl = "https://dev.azure.com/$Org/$Project/_apis/git/repositories/$RepoId/pullRequests/$PrId`?api-version=7.0"

    try {
        $prData = Invoke-RestMethod -Uri $apiUrl -Headers @{ Authorization = "Bearer $Token" } -ErrorAction Stop
    } catch {
        Fail "Failed to fetch PR details from ADO API: $_"
    }

    if (-not $SourceBranch) {
        $SourceBranch = $prData.sourceRefName
        if (-not $SourceBranch) { Fail "ADO API did not return sourceRefName for PR #$PrId. Pass -SourceBranch explicitly." }
        Write-Step "  Source branch: $SourceBranch (from API)"
    }
    if (-not $TargetBranch) {
        $TargetBranch = $prData.targetRefName
        if (-not $TargetBranch) { Fail "ADO API did not return targetRefName for PR #$PrId. Pass -TargetBranch explicitly." }
        Write-Step "  Target branch: $TargetBranch (from API)"
    }

    return @{ SourceBranch = $SourceBranch; TargetBranch = $TargetBranch }
}

<#
.SYNOPSIS
    Normalize a branch name by stripping refs/heads/ prefix.
#>
function Normalize-BranchName {
    param([string]$Branch)
    return $Branch -replace '^refs/heads/', ''
}

<#
.SYNOPSIS
    Write the env file for the container and return its path.
#>
function Write-EnvFile {
    param(
        [hashtable]$Vars
    )

    $envFile = Join-Path ([System.IO.Path]::GetTempPath()) ("pr-review-bot-{0}.env" -f ([guid]::NewGuid()))
    $lines = @(
        $(if ($Vars.PrUrl)  { "PR_URL=$($Vars.PrUrl)" }    else { $null })
        $(if (-not $Vars.PrUrl) { "ADO_ORG=$($Vars.Org)" } else { $null })
        $(if (-not $Vars.PrUrl) { "ADO_PROJECT=$($Vars.Project)" } else { $null })
        $(if (-not $Vars.PrUrl) { "ADO_REPO_ID=$($Vars.RepoId)" } else { $null })
        $(if (-not $Vars.PrUrl) { "PR_ID=$($Vars.PrId)" } else { $null })
        $(if ($Vars.Source)  { "SOURCE_BRANCH=$($Vars.Source)" }  else { $null })
        $(if ($Vars.Target)  { "TARGET_BRANCH=$($Vars.Target)" }  else { $null })
        "REVIEW_LANGUAGE=$($Vars.Language)"
        "FAIL_ON=$($Vars.FailOn)"
        "PI_MODEL=$($Vars.PiModel)"
        "ADO_MCP_AUTH_TOKEN=$($Vars.Token)"
        "OPENAI_API_KEY=$($Vars.OpenAiApiKey)"
        "VOTE_WAITING_ON=$($Vars.VoteWaitingOn)"
        $(if ($Vars.DryRun) { "DRY_RUN=1" } else { $null })
    ) | Where-Object { $_ }

    [System.IO.File]::WriteAllLines(
        $envFile,
        $lines,
        (New-Object System.Text.UTF8Encoding($false))
    )
    return $envFile
}

Export-ModuleMember -Function Write-Step, Fail, Get-ContainerRuntime, Resolve-PrUrl, Get-AdoToken, Invoke-AdoGet, Get-AdoCurrentUser, Get-ActivePullRequests, Find-PrReviewer, Resolve-PrBranches, Normalize-BranchName, Write-EnvFile
