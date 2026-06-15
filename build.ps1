<#
.SYNOPSIS
    Build the PR review bot container image.

.DESCRIPTION
    Standalone build script for the pr-review-bot image. All inputs
    come from environment variables (or ``.env``). The script has no
    required parameters.

.EXAMPLE
    ./build.ps1
    IMAGE_NAME=pr-review-bot:v1.2 PI_VERSION=0.79.1 ./build.ps1

    # Or pin via .env:
    #   IMAGE_NAME=pr-review-bot:v1.2
    #   PI_VERSION=0.79.1
    # ./build.ps1
#>
[CmdletBinding()]
param(
    # Kept for back-compat. If passed, overrides $env:IMAGE_NAME.
    [string] $Image,
    # Kept for back-compat. If passed, overrides $env:PI_VERSION.
    [string] $PiVersion,
    # Kept for back-compat. If passed, overrides the default ``.env`` lookup.
    [string] $EnvFile = ".env"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

Import-Module (Join-Path $PSScriptRoot 'common.psm1') -Force

# Precedence: explicit param (if non-empty) > process env > default.
# Kept as params for back-compat with CI scripts that pass
# ``-Image X -PiVersion Y``; common case is zero-arg invocation.
$Image = if ($PSBoundParameters.ContainsKey('Image') -and $Image) {
    $Image
} else {
    Get-EnvOrDefault -Name 'IMAGE_NAME' -Default 'pr-review-bot:latest'
}
$PiVersion = if ($PSBoundParameters.ContainsKey('PiVersion') -and $PiVersion) {
    $PiVersion
} else {
    Get-EnvOrDefault -Name 'PI_VERSION' -Default '0.79.1'
}

$Runtime = Get-ContainerRuntime
$ContextDir = $PSScriptRoot

Write-Step "Building image $Image from $ContextDir (runtime: $Runtime)"
& $Runtime build `
    --build-arg "PI_VERSION=$PiVersion" `
    -t $Image $ContextDir

if ($LASTEXITCODE -ne 0) { Fail "Build failed." }
Write-Step "Image $Image built successfully"
