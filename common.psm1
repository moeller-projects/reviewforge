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

function Normalize-AdoSegment {
    param(
        [Parameter(Mandatory)][string]$Value,
        [Parameter(Mandatory)][string]$Name
    )

    $normalized = $Value.Trim()
    if (-not $normalized) { Fail "$Name is required." }
    if ($normalized -match '://') {
        Fail "$Name must be the short Azure DevOps name, not a URL: '$Value'"
    }
    if ($normalized -match '[\r\n]') {
        Fail "$Name must not contain line breaks: '$Value'"
    }
    return $normalized.TrimEnd('/')
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
    $Org = Normalize-AdoSegment -Value $Org -Name 'ADO organization'
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
    List repositories visible to the current token.
#>
function Get-AdoRepositories {
    param(
        [Parameter(Mandatory)][string]$Org,
        [string]$Project,
        [Parameter(Mandatory)][string]$Token
    )

    $top = 100
    $allRepositories = @()
    $continuationToken = $null
    $Org = Normalize-AdoSegment -Value $Org -Name 'ADO organization'
    $encodedOrg = [System.Uri]::EscapeDataString($Org)
    $encodedProject = if ($Project) { [System.Uri]::EscapeDataString((Normalize-AdoSegment -Value $Project -Name 'ADO project')) } else { $null }
    $baseUrl = if ($encodedProject) {
        "https://dev.azure.com/$encodedOrg/$encodedProject/_apis/git/repositories"
    } else {
        "https://dev.azure.com/$encodedOrg/_apis/git/repositories"
    }

    do {
        $continuationQuery = if ($continuationToken) { "&continuationToken=$([System.Uri]::EscapeDataString($continuationToken))" } else { "" }
        $apiUrl = "{0}?`$top={1}{2}&api-version=7.0" -f $baseUrl, $top, $continuationQuery
        $responseHeaders = $null

        try {
            [void][System.Uri]::new($apiUrl)
            $response = Invoke-RestMethod `
                -Uri $apiUrl `
                -Headers @{ Authorization = ('Bearer ' + $Token) } `
                -ResponseHeadersVariable responseHeaders `
                -ErrorAction Stop
        } catch {
            $scope = if ($Project) { "project '$Project'" } else { "organization '$Org'" }
            Fail "Failed calling Azure DevOps repository list for ${scope} at URI '$apiUrl': $_"
        }

        $page = @($response.value)
        $allRepositories += $page

        $continuationHeader = if ($responseHeaders) { $responseHeaders['x-ms-continuationtoken'] } else { $null }
        $continuationToken = if ($continuationHeader) { @($continuationHeader)[0] } else { $null }
    } while ($continuationToken)

    return $allRepositories
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
    $Org = Normalize-AdoSegment -Value $Org -Name 'ADO organization'
    $Project = Normalize-AdoSegment -Value $Project -Name 'ADO project'
    $RepoId = Normalize-AdoSegment -Value $RepoId -Name 'ADO repository'

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
    $Org = Normalize-AdoSegment -Value $Org -Name 'ADO organization'
    $Project = Normalize-AdoSegment -Value $Project -Name 'ADO project'
    $RepoId = Normalize-AdoSegment -Value $RepoId -Name 'ADO repository'
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
    Load KEY=VALUE pairs from a .env file into the current process environment.
#>
function Import-DotEnv {
    param(
        [string]$Path
    )

    if (-not $Path) { return }
    if (-not (Test-Path -LiteralPath $Path)) { Fail "Env file not found: $Path" }

    $resolvedPath = (Resolve-Path -LiteralPath $Path).ProviderPath
    foreach ($line in [System.IO.File]::ReadLines($resolvedPath)) {
        $trimmed = $line.Trim()
        if (-not $trimmed -or $trimmed.StartsWith('#')) { continue }
        if ($trimmed -notmatch '^([A-Za-z_][A-Za-z0-9_]*)\s*=\s*(.*)$') { continue }

        $key = $Matches[1]
        $value = $Matches[2].Trim()
        if (($value.StartsWith('"') -and $value.EndsWith('"')) -or ($value.StartsWith("'") -and $value.EndsWith("'"))) {
            $value = $value.Substring(1, $value.Length - 2)
        }

        [System.Environment]::SetEnvironmentVariable($key, $value, 'Process')
    }
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
        "ADO_AUTH_TOKEN=$($Vars.Token)"
        "OPENAI_API_KEY=$($Vars.OpenAiApiKey)"
        "VOTE_WAITING_ON=$($Vars.VoteWaitingOn)"
        $(if ($Vars.DryRun) { "DRY_RUN=1" } elseif ($env:DRY_RUN) { "DRY_RUN=$env:DRY_RUN" } else { $null })
        $(if ($env:DISABLE_CHUNK_REVIEW) { "DISABLE_CHUNK_REVIEW=$env:DISABLE_CHUNK_REVIEW" } else { $null })
        $(if ($env:MAX_DIFF_BYTES) { "MAX_DIFF_BYTES=$env:MAX_DIFF_BYTES" } else { $null })
        $(if ($env:CHUNK_TRIGGER_DIFF_BYTES) { "CHUNK_TRIGGER_DIFF_BYTES=$env:CHUNK_TRIGGER_DIFF_BYTES" } else { $null })
        $(if ($env:POST_MIN_SEVERITY) { "POST_MIN_SEVERITY=$env:POST_MIN_SEVERITY" } else { $null })
        $(if ($env:REVIEW_RUN_ID) { "REVIEW_RUN_ID=$env:REVIEW_RUN_ID" } else { $null })
        $(if ($env:REVIEW_ARTIFACT_ROOT) { "REVIEW_ARTIFACT_ROOT=$env:REVIEW_ARTIFACT_ROOT" } else { $null })
    ) | Where-Object { $_ }

    [System.IO.File]::WriteAllLines(
        $envFile,
        $lines,
        (New-Object System.Text.UTF8Encoding($false))
    )
    return $envFile
}

Export-ModuleMember -Function Write-Step, Fail, Get-ContainerRuntime, Resolve-PrUrl, Get-AdoToken, Invoke-AdoGet, Get-AdoCurrentUser, Get-AdoRepositories, Get-ActivePullRequests, Find-PrReviewer, Resolve-PrBranches, Normalize-AdoSegment, Normalize-BranchName, Import-DotEnv, Write-EnvFile
