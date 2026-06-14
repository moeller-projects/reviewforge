<#
.SYNOPSIS
    Build and run the Python test suite in Docker/Podman.

.DESCRIPTION
    Uses Dockerfile.tests so the host does not need pytest installed.
    By default, the test run enforces a 95% minimum coverage threshold
    on the auto_pr_reviewer package. Pass -CoverageMin 0 to disable the
    threshold (e.g. when iterating locally before coverage catches up).

.EXAMPLE
    ./test.ps1

.EXAMPLE
    ./test.ps1 -NoBuild

.EXAMPLE
    ./test.ps1 -CoverageMin 80      # override the default 95% threshold
#>
[CmdletBinding()]
param(
    [string] $Image = "pr-review-bot-tests:latest",
    [string] $Dockerfile = "Dockerfile.tests",
    [switch] $NoBuild,
    # 95 matches the gate the team agreed on; pass 0 to disable.
    [int] $CoverageMin = 95,
    [string[]] $PytestArgs = @()
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Fail {
    param([string]$Message)
    Write-Host "ERROR: $Message" -ForegroundColor Red
    exit 1
}

function Write-Step {
    param([string]$Message)
    Write-Host "==> $Message" -ForegroundColor Cyan
}

function Get-ContainerRuntime {
    if (Get-Command docker -ErrorAction SilentlyContinue) { return "docker" }
    if (Get-Command podman -ErrorAction SilentlyContinue) { return "podman" }
    Fail "Neither docker nor podman found on PATH."
}

$Runtime = Get-ContainerRuntime
$Root = $PSScriptRoot

if (-not $NoBuild) {
    Write-Step "Building test image $Image with $Dockerfile (runtime: $Runtime)"
    & $Runtime build -f (Join-Path $Root $Dockerfile) -t $Image $Root
    if ($LASTEXITCODE -ne 0) { Fail "Test image build failed." }
}

$Args = @("run", "--rm", $Image)
if ($CoverageMin -gt 0) {
    Write-Step "Coverage threshold: $CoverageMin%"
    $Args += @("python", "-m", "pytest", "--cov=auto_pr_reviewer", "--cov-report=term-missing", "--cov-fail-under=$CoverageMin")
    $Args += $PytestArgs
} elseif ($PytestArgs.Count -gt 0) {
    $Args += @("python", "-m", "pytest")
    $Args += $PytestArgs
} else {
    # Explicit "no coverage threshold, no extra args" → just run pytest.
    $Args += @("python", "-m", "pytest")
}

Write-Step "Running tests in $Image"
& $Runtime @Args
$rc = $LASTEXITCODE
Write-Step "Tests exited with code $rc"
exit $rc
