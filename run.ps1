<# Compatibility wrapper. Prefer `python -m reviewforge.ops run`. #>
[CmdletBinding()]
param(
    [string] $Runtime,
    [string] $PinFile,
    [string] $Image,
    [string] $PrUrl,
    [string] $Org,
    [string] $Project,
    [string] $RepoId,
    [int] $PrId,
    [string] $AdoToken,
    [string] $SourceBranch,
    [string] $TargetBranch,
    [string] $Language,
    [string] $FailOn,
    [string] $VoteWaitingOn,
    [string] $OpenAiApiKey,
    [string] $PiModel,
    [string] $EnvFile = ".env",
    [string] $ContainerName,
    [string] $ArtifactPath,
    [switch] $DryRun,
    [switch] $PrintCommand,
    [switch] $Build,
    [switch] $KeepContainer
)
$ErrorActionPreference = "Stop"
Import-Module (Join-Path $PSScriptRoot 'common.psm1') -Force
$env:PYTHONPATH = "$PSScriptRoot/src" + $(if ($env:PYTHONPATH) { "$([IO.Path]::PathSeparator)$env:PYTHONPATH" } else { "" })
if ($SourceBranch) { $env:SOURCE_BRANCH = $SourceBranch }
if ($TargetBranch) { $env:TARGET_BRANCH = $TargetBranch }
if ($OpenAiApiKey) { $env:OPENAI_API_KEY = $OpenAiApiKey }
$args = @("-m", "reviewforge.ops", "run", "--env-file", $EnvFile)
if ($Runtime) { $args += @("--runtime", $Runtime) }
if ($PinFile) { $args += @("--pin-file", $PinFile) }
if ($Image) { $args += @("--image", $Image) }
if ($PrUrl) { $args += @("--pr-url", $PrUrl) }
if ($Org) { $args += @("--org", $Org) }
if ($Project) { $args += @("--project", $Project) }
if ($RepoId) { $args += @("--repo-id", $RepoId) }
if ($PrId -gt 0) { $args += @("--pr-id", "$PrId") }
if ($AdoToken) { $args += @("--ado-token", $AdoToken) }
if ($Language) { $args += @("--language", $Language) }
if ($FailOn) { $args += @("--fail-on", $FailOn) }
if ($VoteWaitingOn) { $args += @("--vote-waiting-on", $VoteWaitingOn) }
if ($PiModel) { $args += @("--pi-model", $PiModel) }
if ($ContainerName) { $args += @("--container-name", $ContainerName) }
if ($ArtifactPath) { $args += @("--artifact-path", $ArtifactPath) }
if ($PrintCommand) { $args += "--print-command" }
if ($DryRun) { $args += "--dry-run" }
if ($Build) { $args += "--build" }
if ($KeepContainer) { $args += "--keep-container" }
Invoke-ReviewForgeOps -Arguments $args
exit $LASTEXITCODE
