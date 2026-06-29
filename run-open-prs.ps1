<#
.SYNOPSIS
    Run the reviewer against active Azure DevOps pull requests.

.DESCRIPTION
    Discovers active pull requests across one or more projects in the
    given organization, filters by target branch and draft status,
    and invokes run.ps1 once per matching pull request.

    The hardcoded "aveato" project list and "main/master/dev/develop"
    target-branch list from earlier versions are gone: this script
    requires ADO_ORGANIZATION, ADO_PROJECTS, and ADO_TARGET_BRANCHES
    in the environment (typically loaded from a pre-populated
    ``.env`` reference file, e.g. via direnv) or as parameters and
    has no other "default" project
    or branch knowledge. This makes the script portable to any org.

    The ``.env`` file at the repo root is a reference / template,
    NOT auto-loaded. The user is responsible for loading it into the
    process env themselves (e.g. via direnv, ``set -a; source .env;
    set +a`` in bash, or manual exports). The wrapper does forward
    the file path to ``docker run --env-file`` via ``run.ps1`` when
    the file exists, so the per-PR container still sees the same
    curated values.

    Interactive selection: pass -Interactive to choose which
    discovered PRs to review (e.g. ``1,3-5``, ``all``, ``none``).
    Auto-detects a TTY so the prompt only appears when stdin is
    attached to a console.

.PARAMETER Organization
    Azure DevOps organization URL. Reads from ADO_ORGANIZATION env var
    if not given. Required.

.PARAMETER Projects
    Azure DevOps project names to scan. Reads from ADO_PROJECTS env var
    (comma-separated) if not given. Required.

.PARAMETER TargetBranches
    Target branch names to filter by. Reads from ADO_TARGET_BRANCHES
    env var (comma-separated) if not given. Required.

.PARAMETER AdoToken
    Bearer token for Azure DevOps. Optional; if not given, the env
    var ADO_AUTH_TOKEN (or alias ADO_API_KEY) is used.

.PARAMETER DryRun
    Review only; print findings JSON and do not post to Azure DevOps.

.PARAMETER MaxPullRequests
    Optional cap on the number of matching PRs to review. Default: 0
    (no cap).

.PARAMETER Interactive
    Show the discovered PRs and prompt for which to review. Default:
    false. Auto-enabled when stdout is attached to a TTY.

.PARAMETER Build
    Build the image first (replaces the deleted ``run-local.ps1``).

.EXAMPLE
    # Everything from a pre-loaded .env (e.g. ``set -a; source
    # .env; set +a`` in bash, or direnv on Linux/macOS), no params:
    ./run-open-prs.ps1

    # Override projects and pick which to review:
    ./run-open-prs.ps1 -Projects Laekker.Kitchen,Aveato -Interactive

    # Cap to 3 PRs, dry run, no posting:
    ./run-open-prs.ps1 -MaxPullRequests 3 -DryRun

    # Build first, then run:
    ./run-open-prs.ps1 -Build
#>
[CmdletBinding()]
param(
    [string]   $Organization,
    [string[]] $Projects,
    [string[]] $TargetBranches,
    [string]   $AdoToken,
    [int]      $MaxPullRequests = 0,
    [switch]   $DryRun,
    [switch]   $Interactive,
    [switch]   $Build,
    [string]   $EnvFile = ".env",
    [switch] $KeepContainer
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

Import-Module (Join-Path $PSScriptRoot 'common.psm1') -Force

if ($Build) {
    Write-Step "Building image (run-open-prs.ps1 -Build)"
    & (Join-Path $PSScriptRoot 'build.ps1') -EnvFile $EnvFile
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}

if (-not (Get-Command az -ErrorAction SilentlyContinue)) {
    Fail "Azure CLI ('az') was not found on PATH. Install Azure CLI and the azure-devops extension."
}

# Resolve all config. The required keys are organization, projects,
# and target branches — all three are mandatory, no defaults baked
# into the script (hardcoded project/branch lists removed).
# Resolve-ScriptConfig reads the live process env (which the user
# is responsible for populating — e.g. via direnv, ``set -a; source
# .env; set +a``, or manual exports). The ``.env`` file at the
# repo root is a reference / template, NOT auto-loaded. The same
# path is forwarded to ``docker run --env-file`` by ``run.ps1``
# when the file exists, so the per-PR container still picks up
# the curated values.
$envProjects = $env:ADO_PROJECTS
$envBranches = $env:ADO_TARGET_BRANCHES
$cfg = Resolve-ScriptConfig `
    -Parameters @{
        ADO_ORGANIZATION     = $Organization
        ADO_PROJECTS         = $envProjects
        ADO_TARGET_BRANCHES  = $envBranches
        ADO_AUTH_TOKEN       = $AdoToken
    } `
    -Required @('ADO_ORGANIZATION', 'ADO_PROJECTS', 'ADO_TARGET_BRANCHES')

$Organization = $cfg['ADO_ORGANIZATION']
$Projects     = ConvertFrom-CommaList $cfg['ADO_PROJECTS']
$TargetBranches = ConvertFrom-CommaList $cfg['ADO_TARGET_BRANCHES']

# Normalize the organization URL. Trailing slash required by az.
$Organization = $Organization.Trim().TrimEnd('/') + '/'
$Org = if ($Organization -match '^https://dev\.azure\.com/([^/]+)/?') { $Matches[1] }
       elseif ($Organization -match '^https://([^/.]+)\.visualstudio\.com/?') { $Matches[1] }
       else { Fail "Could not derive organization short name from '$Organization'. Expected https://dev.azure.com/{org}/." }

# ADO bearer token: -AdoToken param wins; otherwise the env-var
# chain (ADO_AUTH_TOKEN / ADO_API_KEY) loaded by Resolve-ScriptConfig.
if ($cfg['ADO_AUTH_TOKEN']) {
    [System.Environment]::SetEnvironmentVariable('ADO_AUTH_TOKEN', $cfg['ADO_AUTH_TOKEN'], 'Process')
}
if (-not $env:ADO_AUTH_TOKEN) {
    Write-Step "WARN: `$env:ADO_AUTH_TOKEN is not set. run.ps1 will fail unless it is set or passed via -AdoToken."
}

if (-not $env:OPENAI_API_KEY) {
    Fail 'No model key. Set OPENAI_API_KEY in the environment (e.g. via your .env loader).'
}

# Verify the azure-devops extension is installed.
$extension = & az extension show --name azure-devops --only-show-errors -o json 2>&1
if ($LASTEXITCODE -ne 0) {
    Fail "Azure CLI azure-devops extension is not installed. Run: az extension add --name azure-devops"
}

# Helper: strip ``refs/heads/`` from a branch name. The Python side
# has the same helper; this local copy is for filtering az output.
function script:Normalize-RefName {
    param([string]$Branch)
    if (-not $Branch) { return $Branch }
    return $Branch -replace '^refs/heads/', ''
}

# Helper: safe property access on PSCustomObjects.
function script:Get-ObjectProperty {
    param(
        $InputObject,
        [Parameter(Mandatory)][string]$Name
    )
    if ($null -eq $InputObject) { return $null }
    $property = $InputObject.PSObject.Properties[$Name]
    if (-not $property) { return $null }
    return $property.Value
}

# List active PRs for one project (paginated). Used in the PS 5.1
# fallback. The PS 7+ path inlines this for parallel speedup.
function script:Get-AzPullRequestsForProject {
    param(
        [Parameter(Mandatory)][string]$OrganizationUrl,
        [Parameter(Mandatory)][string]$Project
    )

    $top = 100
    $skip = 0
    $allPullRequests = @()
    do {
        $output = & az repos pr list `
            --organization $OrganizationUrl `
            --project $Project `
            --status active `
            --top $top `
            --skip $skip `
            --only-show-errors -o json 2>&1
        if ($LASTEXITCODE -ne 0) {
            throw "az repos pr list failed for '$Project' (exit $LASTEXITCODE): $output"
        }
        $text = ($output | Out-String).Trim()
        $items = @()
        if ($text) {
            try { $items = @($text | ConvertFrom-Json -Depth 10) }
            catch { throw "az returned invalid JSON for '$Project': $_`n$text" }
        }
        $allPullRequests += $items
        $skip += $items.Count
    } while ($items.Count -eq $top)
    return $allPullRequests
}

# Fetch active PRs for every project concurrently. With N=4
# projects the serial loop paid N x (az handshake + first-page
# latency) = ~1-5s; the parallel cut is bounded by the slowest
# single project. ThrottleLimit is capped at the project count
# to avoid spawning idle runspaces, and the upper bound of 4
# keeps us well under ADO REST throttling.
#
# Note: ForEach-Object -Parallel runs each iteration in an
# isolated runspace, so script-scope helpers aren't visible. We
# re-import common.psm1 inside the block to recover them. The az
# call is inlined to avoid needing Invoke-AzJson. Fail is
# replaced with `throw` so failures propagate back to the
# parent runspace. On PowerShell 5.1 this falls back to the
# sequential helper path via the version check below.
if ($PSVersionTable.PSVersion.Major -ge 7) {
    $throttle = [Math]::Min($Projects.Count, 4)
    $commonModulePath = Join-Path $PSScriptRoot 'common.psm1'
    $AllPullRequests = @(
        $Projects | ForEach-Object -ThrottleLimit $throttle -Parallel {
            $project = $_
            $org = $using:Organization
            Import-Module $using:commonModulePath -Force
            Write-Step "Listing active PRs in '$project'..."
            $top = 100
            $skip = 0
            $threadResults = @()
            do {
                $output = & az repos pr list `
                    --organization $org `
                    --project $project `
                    --status active `
                    --top $top `
                    --skip $skip `
                    --only-show-errors `
                    -o json 2>&1
                if ($LASTEXITCODE -ne 0) {
                    throw "az repos pr list failed for '$project' (exit $LASTEXITCODE): $output"
                }
                $text = ($output | Out-String).Trim()
                $items = @()
                if ($text) {
                    try { $items = @($text | ConvertFrom-Json -Depth 10) }
                    catch { throw "az returned invalid JSON for '$project': $_`n$text" }
                }
                foreach ($item in $items) {
                    $threadResults += [pscustomobject]@{
                        PullRequest = $item
                        Project     = $project
                    }
                }
                $skip += $items.Count
            } while ($items.Count -eq $top)
            $threadResults
        }
    )
} else {
    # PowerShell 5.1 fallback.
    $AllPullRequests = @(
        foreach ($project in $Projects) {
            Write-Step "Listing active PRs in '$project'..."
            foreach ($pullRequest in @(Get-AzPullRequestsForProject `
                -OrganizationUrl $Organization `
                -Project $project)) {
                [pscustomobject]@{
                    PullRequest = $pullRequest
                    Project     = $project
                }
            }
        }
    )
}

# Dedupe + filter. Keyed by (project, repoId, prId) so cross-project
# PRs on repos with the same name don't collide.
$seen = @{}
$SelectedPullRequests = @(foreach ($pullRequestEntry in $AllPullRequests) {
    $pullRequest = $pullRequestEntry.PullRequest
    $repository = Get-ObjectProperty -InputObject $pullRequest -Name 'repository'
    $projectName = $pullRequestEntry.Project
    $repoId = @(
        (Get-ObjectProperty -InputObject $repository -Name 'id'),
        (Get-ObjectProperty -InputObject $repository -Name 'name')
    ) | Where-Object { $_ } | Select-Object -First 1
    $repoName = @(
        (Get-ObjectProperty -InputObject $repository -Name 'name'),
        $repoId
    ) | Where-Object { $_ } | Select-Object -First 1
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
}) | Sort-Object Project, RepoName, Target, { Get-ObjectProperty -InputObject $_.PullRequest -Name 'pullRequestId' }

if ($MaxPullRequests -gt 0) {
    $SelectedPullRequests = @($SelectedPullRequests | Select-Object -First $MaxPullRequests)
}

if (-not $SelectedPullRequests) {
    Write-Step "No active pull requests found on target branches: $($TargetBranches -join ', ')."
    exit 0
}

Write-Step ("Found {0} active pull request(s)." -f $SelectedPullRequests.Count)

# Interactive selection. Auto-enable only when caller did not
# pass -Interactive and stdin/stdout are attached to TTY.
$isTty = -not [Console]::IsInputRedirected
if (-not $PSBoundParameters.ContainsKey('Interactive') -and $isTty -and -not [Console]::IsOutputRedirected) {
    $Interactive = $true
}
if ($Interactive) {
    $labels = $SelectedPullRequests | ForEach-Object {
        $pr = $_.PullRequest
        $prId = [int](Get-ObjectProperty -InputObject $pr -Name 'pullRequestId')
        $title = Get-ObjectProperty -InputObject $pr -Name 'title'
        "PR #$prId  $($_.Project)/$($_.RepoName) -> $($_.Target)  $title"
    }
    $picks = Show-InteractivePrompt -Items $labels -Prompt "==> Select PRs to review"
    if ($picks.Count -eq 0) {
        Write-Step "No PRs selected. Exiting."
        exit 0
    }
    $SelectedPullRequests = @($SelectedPullRequests | Select-Object -Index ($picks | ForEach-Object { $_ - 1 }))
    Write-Step ("Selected {0} pull request(s) for review." -f $SelectedPullRequests.Count)
}

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
        -AdoToken $env:ADO_AUTH_TOKEN `
        -DryRun:$DryRun `
        -KeepContainer:$KeepContainer `
        -EnvFile $EnvFile

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
