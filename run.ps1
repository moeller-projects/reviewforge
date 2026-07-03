<#
.SYNOPSIS
    Run the PR review bot against an Azure DevOps pull request.

.DESCRIPTION
    Thin Docker orchestrator. All ADO logic, token resolution, branch
    normalization, PR URL parsing, and REST calls live in the Python
    package ``auto_pr_reviewer`` and are executed inside the container.
    This script only:

      * reads CLI args / env vars via Resolve-ScriptConfig,
      * picks a container runtime (docker or podman),
      * runs the container detached and returns the spawn exit code.

    Pass just a PR URL and the container resolves everything (org,
    project, repo, PR id, branches) internally via the ADO REST API.
    Alternatively, pass individual params (-Org, -Project, -RepoId,
    -PrId) and the container will still auto-resolve branches unless
    they are overridden.

    Use -DryRun to review only (print findings JSON) without posting
    to the PR. Use -Build to build the image first (replaces the
    former ``run-local.ps1``).

    The ``.env`` file at the repo root is a reference / template,
    NOT auto-loaded. The user is responsible for loading it into the
    process env themselves (e.g. via direnv, ``set -a; source .env;
    set +a`` in bash, or manual exports). The wrapper does forward
    the file path to ``docker run --env-file`` when the file exists,
    so the per-PR container still sees the same curated values.

.EXAMPLE
    # Simplest invocation — just the PR URL (container resolves everything):
    ./run.ps1 -PrUrl "https://dev.azure.com/contoso/Payments/_git/payments-api/pullrequest/1423"

    # Dry run to iterate on prompts without posting:
    ./run.ps1 -PrUrl "https://dev.azure.com/contoso/Payments/_git/payments-api/pullrequest/1423" -DryRun

    # Build the image first (replaces ./run-local.ps1):
    ./run.ps1 -PrUrl "..." -Build

    # All config in shell env (or pre-loaded .env), no params:
    ./run.ps1
#>
[CmdletBinding()]
param(
    [string] $PrUrl,
    [string] $Org,
    [string] $Project,
    [string] $RepoId,
    [int]    $PrId,
    [string] $AdoToken,
    [string] $EnvFile = ".env",
    [switch] $DryRun,
    [switch] $Build,
    [switch] $KeepContainer
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

Import-Module (Join-Path $PSScriptRoot 'common.psm1') -Force

# Optional build step. Replaces the deleted run-local.ps1 shim.
if ($Build) {
    Write-Step "Building image (run.ps1 -Build)"
    & (Join-Path $PSScriptRoot 'build.ps1') -EnvFile $EnvFile
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}

# Resolve all config. Resolve-ScriptConfig reads the live process
# env (which the user is responsible for populating — e.g. via
# direnv, ``set -a; source .env; set +a``, or manual exports) and
# layers explicit -Parameters overrides on top. The ``.env`` file
# at the repo root is a reference / template, NOT auto-loaded.
# Required keys: ADO_AUTH_TOKEN (or one of its aliases) and one
# of: OPENAI_API_KEY env var, OR ~/.pi/agent/auth.json (Pi's
# native credential store — populated by `pi` → /login, supports
# API keys AND subscription OAuth like ChatGPT Plus/Pro Codex).
# Other keys are forwarded to the container as env vars only.
$cfg = Resolve-ScriptConfig `
    -Parameters @{
        PR_URL          = $PrUrl
        ADO_ORG         = $Org
        ADO_PROJECT     = $Project
        ADO_REPO_ID     = $RepoId
        PR_ID           = if ($PrId -gt 0) { "$PrId" } else { $null }
        ADO_AUTH_TOKEN  = $AdoToken
    }

# Resolve the bearer token via the alias chain. Python package does
# the same. We surface a clear "missing" message here so the user
# doesn't see the container's runtime error.
$token = $cfg['ADO_AUTH_TOKEN']
if (-not $token) { $token = $cfg['ADO_API_KEY'] }
if (-not $token) {
    Fail 'No ADO bearer token. Set ADO_AUTH_TOKEN (or ADO_API_KEY) in .env, or pass -AdoToken.'
}
# Pi also reads ~/.pi/agent/auth.json — accept it as a stand-in
# for OPENAI_API_KEY so ChatGPT Plus/Pro subscription OAuth (and
# any other provider the user logged into via `pi` /login) works.
$authJsonPath = $null
$homeRoot = if ($HOME) { $HOME } elseif ($env:USERPROFILE) { $env:USERPROFILE } else { $null }
if ($homeRoot) {
    $candidate = Join-Path $homeRoot '.pi/agent/auth.json'
    if (Test-Path -LiteralPath $candidate) {
        $authJsonPath = (Resolve-Path -LiteralPath $candidate).Path
    }
}
if (-not $cfg['OPENAI_API_KEY'] -and -not $authJsonPath) {
    Fail 'No model credentials. Set OPENAI_API_KEY in .env, or create ~/.pi/agent/auth.json (run `pi` locally and use /login).'
}

# DRY_RUN is a switch in PowerShell, an env var elsewhere. The
# container reads the env var; we mirror the param into the env so
# --env-file and the in-process Apply-Env step see the same value.
if ($PSBoundParameters.ContainsKey('DryRun') -and $DryRun) {
    [System.Environment]::SetEnvironmentVariable('DRY_RUN', '1', 'Process')
    $cfg['DRY_RUN'] = '1'
}

$Runtime = Get-ContainerRuntime
$Image = $cfg['IMAGE_NAME']
if (-not $Image) { $Image = $cfg['IMAGE'] }
if (-not $Image) { $Image = 'pr-review-bot:latest' }

# Artifact storage. Local path takes priority (set in .env as
# ARTIFACT_PATH). Otherwise we use a named volume shared across runs.
$ArtifactPath = $cfg['ARTIFACT_PATH']
$useNamedVolume = [string]::IsNullOrWhiteSpace($ArtifactPath)
$ArtifactVolumeName = $null
if ($useNamedVolume) {
    $ArtifactVolumeName = $cfg['REVIEW_ARTIFACT_VOLUME_NAME']
    if (-not $ArtifactVolumeName) { $ArtifactVolumeName = 'pr-review-bot-artifacts' }
    Write-Step "Ensuring artifact volume '$ArtifactVolumeName' exists"
    & $Runtime volume create $ArtifactVolumeName 2>&1 | Out-Null
    Write-Step "Artifact volume '$ArtifactVolumeName' ready"
} else {
    $ArtifactPath = [System.IO.Path]::GetFullPath($ArtifactPath)
    if (-not (Test-Path -LiteralPath $ArtifactPath)) {
        Write-Step "Creating artifact directory: $ArtifactPath"
        New-Item -Path $ArtifactPath -ItemType Directory -Force | Out-Null
    }
    Write-Step "Using local artifact path: $ArtifactPath"
}

# Env-file. Prefer Docker's native ``--env-file`` so secrets are
# never copied to a temp file. When .env is missing, fall back to a
# temp env file built from the **full** process env snapshot so
# user-set vars outside the 23-key canonical list (e.g. LANG,
# HTTP_PROXY) still reach the container. The Python container
# ignores keys it doesn't know about.
$envFileInfo = if (Test-Path -LiteralPath $EnvFile) {
    Get-ReviewerEnvFile -EnvFile $EnvFile
} else {
    $fullSnapshot = @{}
    [System.Environment]::GetEnvironmentVariables('Process').GetEnumerator() |
        ForEach-Object { $fullSnapshot[$_.Key] = $_.Value }
    Get-ReviewerEnvFile -EnvFile "" -ProcessEnvSnapshot $fullSnapshot
}
if ($envFileInfo.IsTemp) {
    Write-Step "Using temp env-file (no .env found): $($envFileInfo.Path)"
} else {
    Write-Step "Using .env file: $($envFileInfo.Path)"
}

$ContainerName = $cfg['CONTAINER_NAME']
if (-not $ContainerName -and $cfg['PR_ID']) {
    $ContainerName = "pr-review-bot-pr-$($cfg['PR_ID'])"
}

$runLabel = if ($cfg['PR_URL']) {
    "Running reviewer with PR_URL (container resolves identity){0} [runtime: {1}]" -f $(if($DryRun){' [dry run]'}else{''}), $Runtime
} else {
    "Running reviewer on PR #{0}{1} [runtime: {2}]" -f $cfg['PR_ID'], $(if($DryRun){' [dry run]'}else{''}), $Runtime
}

# Build the docker invocation.
$dockerArgs = @("run", "--network", "host")
if ($Runtime -eq "podman") {
    $dockerArgs = @("run", "--network", "bridge", "--dns", "8.8.8.8", "--dns", "1.1.1.1")
}
if (-not $KeepContainer) {
    $dockerArgs += "--rm"
    # Detached mode (per project decision: -d kept). The spawn exit
    # code is what the script reports; the container's review result
    # is not observable from this script.
    $dockerArgs += @("-d")
}
if ($ContainerName) { $dockerArgs += @("--name", $ContainerName) }

if ($useNamedVolume) {
    $dockerArgs += @("--volume", "$($ArtifactVolumeName):/workspace/artifacts")
} else {
    # Convert Windows path to WSL path for Podman.
    $containerPath = $ArtifactPath
    if ($Runtime -eq "podman" -and $ArtifactPath -match '^[A-Z]:') {
        $containerPath = $ArtifactPath -replace '^([A-Z]):', '/$1' -replace '\\', '/'
    }
    $dockerArgs += @("--volume", "$($containerPath):/workspace/artifacts")
}

# Mount Pi's auth.json if present on the host, so subscription
# OAuth tokens (ChatGPT Plus/Pro Codex, Claude Pro/Max, GitHub
# Copilot, …) are visible inside the container. Pi reads
# /root/.pi/agent/auth.json because the image runs as root.
# Read-write: subscription OAuth auto-refresh writes back here,
# and `:ro` would break token renewal on long runs.
if ($authJsonPath) {
    Write-Step "Mounting Pi auth.json: $authJsonPath"
    $dockerArgs += @("--volume", "${authJsonPath}:/root/.pi/agent/auth.json")
}

$dockerArgs += @("--env-file", $envFileInfo.Path)

# Per-invocation overrides passed as ``-e`` so they win over
# --env-file. The Python package resolves the same env vars, so
# these are the canonical pre-run configuration.
$overrides = @{
    'ADO_AUTH_TOKEN'  = $token
    'PR_URL'          = $cfg['PR_URL']
    'ADO_ORG'         = $cfg['ADO_ORG']
    'ADO_PROJECT'     = $cfg['ADO_PROJECT']
    'ADO_REPO_ID'     = $cfg['ADO_REPO_ID']
    'PR_ID'           = $cfg['PR_ID']
    'SOURCE_BRANCH'   = $cfg['SOURCE_BRANCH']
    'TARGET_BRANCH'   = $cfg['TARGET_BRANCH']
    'REVIEW_LANGUAGE' = $cfg['REVIEW_LANGUAGE']
    'FAIL_ON'         = $cfg['FAIL_ON']
    'VOTE_WAITING_ON' = $cfg['VOTE_WAITING_ON']
    'PI_MODEL'        = $cfg['PI_MODEL']
    'DRY_RUN'         = $cfg['DRY_RUN']
}
foreach ($k in $overrides.Keys) {
    $v = $overrides[$k]
    if ($v) { $dockerArgs += @("-e", "$k=$v") }
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
