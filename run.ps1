<# Compatibility wrapper. Prefer `python -m reviewforge.ops run`. #>
[CmdletBinding()]
param(
    [string] $PrUrl,
    [string] $Org,
    [string] $Project,
    [string] $RepoId,
    [int] $PrId,
    [string] $AdoToken,
    [string] $EnvFile = ".env",
    [switch] $DryRun,
    [switch] $Build,
    [switch] $KeepContainer
)
$ErrorActionPreference = "Stop"
$env:PYTHONPATH = "$PSScriptRoot/src" + $(if ($env:PYTHONPATH) { ";$env:PYTHONPATH" } else { "" })
$args = @("-m", "reviewforge.ops", "run", "--env-file", $EnvFile)
if ($PrUrl) { $args += @("--pr-url", $PrUrl) }
if ($Org) { $args += @("--org", $Org) }
if ($Project) { $args += @("--project", $Project) }
if ($RepoId) { $args += @("--repo-id", $RepoId) }
if ($PrId -gt 0) { $args += @("--pr-id", "$PrId") }
if ($AdoToken) { $args += @("--ado-token", $AdoToken) }
if ($DryRun) { $args += "--dry-run" }
if ($Build) { $args += "--build" }
if ($KeepContainer) { $args += "--keep-container" }
& python @args
exit $LASTEXITCODE
