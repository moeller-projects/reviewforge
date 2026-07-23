<# Compatibility wrapper. Prefer `python -m reviewforge.ops run-open-prs`. #>
[CmdletBinding()]
param(
    [string] $Organization,
    [string[]] $Projects,
    [string[]] $TargetBranches,
    [string] $AdoToken,
    [int] $MaxPullRequests = 0,
    [switch] $DryRun,
    [switch] $Interactive,
    [switch] $Build,
    [string] $EnvFile = ".env",
    [switch] $KeepContainer
)
$ErrorActionPreference = "Stop"
$env:PYTHONPATH = "$PSScriptRoot/src" + $(if ($env:PYTHONPATH) { "$([IO.Path]::PathSeparator)$env:PYTHONPATH" } else { "" })
$args = @("-m", "reviewforge.ops", "run-open-prs", "--env-file", $EnvFile)
if ($Organization) { $args += @("--organization", $Organization) }
if ($Projects) { $args += @("--projects", ($Projects -join ',')) }
if ($TargetBranches) { $args += @("--target-branches", ($TargetBranches -join ',')) }
if ($AdoToken) { $args += @("--ado-token", $AdoToken) }
if ($MaxPullRequests -gt 0) { $args += @("--max-pull-requests", "$MaxPullRequests") }
if ($Interactive) { $args += "--interactive" }
if ($DryRun) { $args += "--dry-run" }
if ($Build) { $args += "--build" }
if ($KeepContainer) { $args += "--keep-container" }
& python @args
exit $LASTEXITCODE
