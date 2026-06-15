<#
.SYNOPSIS
    Run the PR review bot against an Azure DevOps pull request.

.DESCRIPTION
    Thin Docker orchestrator. All ADO logic, token resolution, branch
    normalization, PR URL parsing, and REST calls live in the Python
    package ``auto_pr_reviewer`` and are executed inside the container.
    This script only:

      * reads CLI args and forwards them as env vars,
      * optionally loads a ``.env`` file into the process env,
      * picks a container runtime (docker or podman),
      * runs the container with the env vars and cleans up the temp
        env file in a ``finally`` block.

    Pass just a PR URL and the container resolves everything (org,
    project, repo, PR id, branches) internally via the ADO REST API.
    Alternatively, pass individual params (-Org, -Project, -RepoId,
    -PrId) and the container will still auto-resolve branches unless
    they are overridden.

    Use -DryRun to review only (print findings JSON) without posting
    to the PR.

.PARAMETER PrUrl
    Full Azure DevOps pull-request URL. When provided, the container handles
    all resolution internally — no other identity params are needed.
    Supported formats:
      https://dev.azure.com/{org}/{project}/_git/{repo}/pullrequest/{id}
      https://{org}.visualstudio.com/{project}/_git/{repo}/pullrequest/{id}

.PARAMETER Org
    Azure DevOps organization SHORT name (e.g. "contoso"). Required if -PrUrl
    is not given; otherwise auto-detected from the URL.

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
    Bearer token for ADO. If omitted, the script forwards whatever
    ``$env:ADO_AUTH_TOKEN`` is set to (or ``ADO_MCP_AUTH_TOKEN`` /
    ``ADO_API_KEY`` as aliases — see the Python package for the full
    resolution order). The container does not acquire tokens itself.

.PARAMETER OpenAiApiKey
    Model provider key for Pi. Defaults to $env:OPENAI_API_KEY.

.PARAMETER PiModel
    Model pattern Pi should use. Default: openai/gpt-5.5.

.PARAMETER Image
    Docker/Podman image tag. Default: pr-review-bot:latest.

.PARAMETER ContainerName
    Optional Docker/Podman container name. Defaults to pr-review-bot-pr-{PrId} when -PrId is provided.

.PARAMETER ArtifactPath
    Local directory path for review artifacts. When provided, this directory is mounted
    into the container instead of using a named volume. Useful for inspecting artifacts
    after the run. Example: -ArtifactPath "$PWD/artifacts"

.PARAMETER EnvFile
    Optional path to a ``.env`` file. Default: ``.env`` in the current
    directory. When the file exists, it is passed directly to
    ``docker run --env-file`` (Docker's native behavior — secrets are
    never copied to a temp file). When the file is missing or this
    parameter is empty, the script falls back to a temp env file built
    from the process env so shell-set vars still reach the container.

.PARAMETER DryRun
    Review only; print the findings JSON and do not post to the PR.

.EXAMPLE
    # Simplest invocation — just the PR URL (container resolves everything):
    ./run.ps1 -PrUrl "https://dev.azure.com/contoso/Payments/_git/payments-api/pullrequest/1423"

    # Dry run to iterate on prompts without posting:
    ./run.ps1 -PrUrl "https://dev.azure.com/contoso/Payments/_git/payments-api/pullrequest/1423" -DryRun

    # Individual params (branches still auto-resolved by the container):
    ./run.ps1 -Org contoso -Project Payments -RepoId payments-api -PrId 1423

    # German review comments:
    ./run.ps1 -PrUrl "https://dev.azure.com/contoso/Payments/_git/payments-api/pullrequest/1423" -Language German
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
    [string] $ContainerName,
    [string] $ArtifactPath,
    [string] $EnvFile = ".env",
    [switch] $DryRun
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

Import-Module (Join-Path $PSScriptRoot 'common.psm1') -Force

# Layer 1: load .env (if any) into the process env.  Process env wins.
if ($EnvFile -and (Test-Path -LiteralPath $EnvFile)) {
    Import-DotEnv -Path $EnvFile
}
if (-not $PSBoundParameters.ContainsKey('PrUrl') -and $env:PR_URL) { $PrUrl = $env:PR_URL }
if (-not $PSBoundParameters.ContainsKey('Org') -and $env:ADO_ORG) { $Org = $env:ADO_ORG }
if (-not $PSBoundParameters.ContainsKey('Project') -and $env:ADO_PROJECT) { $Project = $env:ADO_PROJECT }
if (-not $PSBoundParameters.ContainsKey('RepoId') -and $env:ADO_REPO_ID) { $RepoId = $env:ADO_REPO_ID }
if (-not $PSBoundParameters.ContainsKey('PrId') -and $env:PR_ID) { $PrId = [int]$env:PR_ID }
if (-not $PSBoundParameters.ContainsKey('SourceBranch') -and $env:SOURCE_BRANCH) { $SourceBranch = $env:SOURCE_BRANCH }
if (-not $PSBoundParameters.ContainsKey('TargetBranch') -and $env:TARGET_BRANCH) { $TargetBranch = $env:TARGET_BRANCH }
if (-not $PSBoundParameters.ContainsKey('Language') -and $env:REVIEW_LANGUAGE) { $Language = $env:REVIEW_LANGUAGE }
if (-not $PSBoundParameters.ContainsKey('FailOn') -and $env:FAIL_ON) { $FailOn = $env:FAIL_ON }
if (-not $PSBoundParameters.ContainsKey('VoteWaitingOn') -and $env:VOTE_WAITING_ON) { $VoteWaitingOn = $env:VOTE_WAITING_ON }
if (-not $PSBoundParameters.ContainsKey('AdoToken') -and $env:ADO_API_KEY) { $AdoToken = $env:ADO_API_KEY }
if (-not $PSBoundParameters.ContainsKey('AdoToken') -and $env:ADO_AUTH_TOKEN) { $AdoToken = $env:ADO_AUTH_TOKEN }
if (-not $PSBoundParameters.ContainsKey('OpenAiApiKey') -and $env:OPENAI_API_KEY) { $OpenAiApiKey = $env:OPENAI_API_KEY }
if (-not $PSBoundParameters.ContainsKey('PiModel') -and $env:PI_MODEL) { $PiModel = $env:PI_MODEL }
if (-not $PSBoundParameters.ContainsKey('Image') -and $env:IMAGE_NAME) { $Image = $env:IMAGE_NAME }
if (-not $PSBoundParameters.ContainsKey('Image') -and $env:IMAGE) { $Image = $env:IMAGE }
if (-not $PSBoundParameters.ContainsKey('ContainerName') -and $env:CONTAINER_NAME) { $ContainerName = $env:CONTAINER_NAME }
if (-not $PSBoundParameters.ContainsKey('ArtifactPath') -and $env:ARTIFACT_PATH) { $ArtifactPath = $env:ARTIFACT_PATH }
if (-not $PSBoundParameters.ContainsKey('DryRun') -and $env:DRY_RUN) { $DryRun = $env:DRY_RUN -in @('1','true','True','yes','on') }

# Layer 2: CLI args win over process env.
function Apply-Env {
    param([string]$Name, [string]$Value)
    if ($PSBoundParameters.ContainsKey($Name) -and $Value) {
        [System.Environment]::SetEnvironmentVariable($Name, $Value, 'Process')
    }
}
Apply-Env 'PrUrl'          $PrUrl
Apply-Env 'ADO_ORG'        $Org
Apply-Env 'ADO_PROJECT'    $Project
Apply-Env 'ADO_REPO_ID'    $RepoId
Apply-Env 'PR_ID'          $(if ($PrId -gt 0) { "$PrId" } else { $null })
Apply-Env 'SOURCE_BRANCH'  $SourceBranch
Apply-Env 'TARGET_BRANCH'  $TargetBranch
Apply-Env 'REVIEW_LANGUAGE' $Language
Apply-Env 'FAIL_ON'        $FailOn
Apply-Env 'VOTE_WAITING_ON' $VoteWaitingOn
Apply-Env 'PI_MODEL'       $PiModel
Apply-Env 'ADO_AUTH_TOKEN' $AdoToken
Apply-Env 'OPENAI_API_KEY' $OpenAiApiKey
Apply-Env 'IMAGE_NAME'     $Image
Apply-Env 'CONTAINER_NAME' $ContainerName
if ($PSBoundParameters.ContainsKey('DryRun') -and $env:DRY_RUN) {
    [System.Environment]::SetEnvironmentVariable('DRY_RUN', '1', 'Process')
}

# Layer 3: prerequisites.
if (-not $env:OPENAI_API_KEY) {
    Fail 'No model key. Set $env:OPENAI_API_KEY or pass -OpenAiApiKey.'
}

$Runtime = Get-ContainerRuntime
# ADO bearer token resolution is handled by the Python container via
# $env:ADO_AUTH_TOKEN (or the ADO_API_KEY / ADO_MCP_AUTH_TOKEN aliases).
# The wrapper layers above (Apply-Env, --env-file, -e ADO_AUTH_TOKEN=...)
# already forward the value; the container emits a clear error if it's
# missing, so no extra PowerShell-side acquisition is required.
$ArtifactVolumeName = $env:REVIEW_ARTIFACT_VOLUME_NAME
if (-not $ArtifactVolumeName) {
    $ArtifactVolumeName = 'pr-review-bot-artifacts'
}

# --- Prepare artifact storage (named volume or local path) -------------------
$useNamedVolume = -not $ArtifactPath
$ArtifactVolumeName = $null

if ($useNamedVolume) {
    $ArtifactVolumeName = $env:REVIEW_ARTIFACT_VOLUME_NAME
    if (-not $ArtifactVolumeName) {
        $ArtifactVolumeName = 'pr-review-bot-artifacts'
    }

    Write-Step "Ensuring artifact volume '$ArtifactVolumeName' exists"
    # volume create is idempotent in Docker/Podman; ignore "already exists" error
    & $Runtime volume create $ArtifactVolumeName 2>&1 | Out-Null
    # Exit code may be non-zero if volume already exists - that's OK
    Write-Step "Artifact volume '$ArtifactVolumeName' ready"
} else {
    # Validate and create local path if needed
    $ArtifactPath = [System.IO.Path]::GetFullPath($ArtifactPath)
    if (-not (Test-Path -LiteralPath $ArtifactPath)) {
        Write-Step "Creating artifact directory: $ArtifactPath"
        New-Item -Path $ArtifactPath -ItemType Directory -Force | Out-Null
    }
    Write-Step "Using local artifact path: $ArtifactPath"
}

# Layer 4: build the Docker invocation.
#
# Env-file: prefer Docker's native `--env-file .env` behavior. When a
# ``.env`` file exists (the default ``$EnvFile = '.env'``), pass it
# straight to Docker so secrets are never copied into a temp file.
# When no ``.env`` is available, fall back to a temp env file built
# from the process env so shell-set vars still reach the container.
# Only the secrets / per-invocation overrides are passed as `-e` so
# they win over whatever the env-file said.

# Snapshot of every env var the container should see via the temp
# fallback. Pulled from the process env that Layers 1-3 populated.
$tempEnvSnapshot = @{
    PR_URL            = $env:PR_URL
    ADO_ORG           = $env:ADO_ORG
    ADO_PROJECT       = $env:ADO_PROJECT
    ADO_REPO_ID       = $env:ADO_REPO_ID
    PR_ID             = $env:PR_ID
    SOURCE_BRANCH     = $env:SOURCE_BRANCH
    TARGET_BRANCH     = $env:TARGET_BRANCH
    ADO_AUTH_TOKEN    = $env:ADO_AUTH_TOKEN
    OPENAI_API_KEY    = $env:OPENAI_API_KEY
    REVIEW_LANGUAGE   = $env:REVIEW_LANGUAGE
    FAIL_ON           = $env:FAIL_ON
    VOTE_WAITING_ON   = $env:VOTE_WAITING_ON
    PI_MODEL          = $env:PI_MODEL
    DRY_RUN           = $env:DRY_RUN
    DISABLE_CHUNK_REVIEW = $env:DISABLE_CHUNK_REVIEW
    MAX_DIFF_BYTES    = $env:MAX_DIFF_BYTES
    CHUNK_TRIGGER_DIFF_BYTES = $env:CHUNK_TRIGGER_DIFF_BYTES
    POST_MIN_SEVERITY = $env:POST_MIN_SEVERITY
    REQUIRE_CONTEXT_FOR = $env:REQUIRE_CONTEXT_FOR
    DROP_LOW_CONFIDENCE = $env:DROP_LOW_CONFIDENCE
    MAX_FINDINGS      = $env:MAX_FINDINGS
    CONTEXT_FILE_MAX_LINES = $env:CONTEXT_FILE_MAX_LINES
    CONTEXT_SEARCH_MAX_MATCHES = $env:CONTEXT_SEARCH_MAX_MATCHES
    REVIEW_RUN_ID     = $env:REVIEW_RUN_ID
    REVIEW_ARTIFACT_ROOT = $env:REVIEW_ARTIFACT_ROOT
}

$envFileInfo = Get-ReviewerEnvFile -EnvFile $EnvFile -ProcessEnvSnapshot $tempEnvSnapshot
if ($envFileInfo.IsTemp) {
    Write-Step "Using temp env-file (no .env found): $($envFileInfo.Path)"
} else {
    Write-Step "Using .env file: $($envFileInfo.Path)"
}

$runLabel = if ($env:PR_URL) {
    "Running reviewer with PR_URL (container resolves identity){0} [runtime: {1}]" -f $(if($DryRun){" [dry run]"}else{""}), $Runtime
} else {
    "Running reviewer on PR #{0}{1} [runtime: {2}]" -f $env:PR_ID, $(if($DryRun){" [dry run]"}else{""}), $Runtime
}

if (-not $ContainerName -and $env:PR_ID) {
    $ContainerName = "pr-review-bot-pr-$($env:PR_ID)"
}

$dockerArgs = @("run", "--rm", "--network", "host")
if ($Runtime -eq "podman") {
    $dockerArgs = @("run", "--rm", "--network", "bridge", "--dns", "8.8.8.8", "--dns", "1.1.1.1")
}
if ($ContainerName) {
    $dockerArgs += @("--name", $ContainerName)
}

$dockerArgs += @("-d")

# Mount artifact storage (named volume or local path)
if ($useNamedVolume) {
    $dockerArgs += @("--volume", "$($ArtifactVolumeName):/workspace/artifacts")
} else {
    # Convert Windows path to WSL path for Podman if needed
    $containerPath = $ArtifactPath
    if ($Runtime -eq "podman" -and $ArtifactPath -match '^[A-Z]:') {
        # Convert C:\path to /c/path for Podman
        $containerPath = $ArtifactPath -replace '^([A-Z]):', '/$1' -replace '\\', '/'
    }
    $dockerArgs += @("--volume", "$($containerPath):/workspace/artifacts")
}

$dockerArgs += @("--env-file", $envFileInfo.Path)

# Dynamic CLI overrides: passed as -e so they win over --env-file.
# These are secrets / per-invocation values the user passes explicitly.
if ($AdoToken)       { $dockerArgs += @("-e", "ADO_AUTH_TOKEN=$AdoToken") }
if ($OpenAiApiKey)   { $dockerArgs += @("-e", "OPENAI_API_KEY=$OpenAiApiKey") }
if ($PrUrl)          { $dockerArgs += @("-e", "PR_URL=$PrUrl") }
if ($Org)            { $dockerArgs += @("-e", "ADO_ORG=$Org") }
if ($Project)        { $dockerArgs += @("-e", "ADO_PROJECT=$Project") }
if ($RepoId)         { $dockerArgs += @("-e", "ADO_REPO_ID=$RepoId") }
if ($PrId -gt 0)     { $dockerArgs += @("-e", "PR_ID=$PrId") }
if ($SourceBranch)   { $dockerArgs += @("-e", "SOURCE_BRANCH=$SourceBranch") }
if ($TargetBranch)   { $dockerArgs += @("-e", "TARGET_BRANCH=$TargetBranch") }
if ($Language)       { $dockerArgs += @("-e", "REVIEW_LANGUAGE=$Language") }
if ($FailOn)         { $dockerArgs += @("-e", "FAIL_ON=$FailOn") }
if ($VoteWaitingOn)  { $dockerArgs += @("-e", "VOTE_WAITING_ON=$VoteWaitingOn") }
if ($PiModel)        { $dockerArgs += @("-e", "PI_MODEL=$PiModel") }
if ($PSBoundParameters.ContainsKey('DryRun') -and $DryRun) {
    $dockerArgs += @("-e", "DRY_RUN=1")
}

$dockerArgs += $Image

Write-Step $runLabel
try {
    & $Runtime @dockerArgs
    $rc = $LASTEXITCODE
} finally {
    if ($envFileInfo.IsTemp) {
        Remove-Item -LiteralPath $envFileInfo.Path -Force -ErrorAction SilentlyContinue
    }
}
Write-Step "Container exited with code $rc"
exit $rc
