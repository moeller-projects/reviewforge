<#
.SYNOPSIS
    Build the PR review bot container image.

.DESCRIPTION
    Standalone build script for the pr-review-bot image. Separated from the run
    script so you can build once and run many times without rebuilding.

.PARAMETER Image
    Docker/Podman image tag. Default: pr-review-bot:latest.

.PARAMETER PiVersion
    Pin the @earendil-works/pi-coding-agent version. Default: 0.79.1.

.PARAMETER AdoMcpVersion
    Pin the @azure-devops/mcp version. Default: 2.7.0.

.EXAMPLE
    ./scripts/build.ps1
    ./scripts/build.ps1 -Image pr-review-bot:v1.2
#>
[CmdletBinding()]
param(
    [string] $Image         = "pr-review-bot:latest",
    [string] $PiVersion     = "0.79.1",
    [string] $AdoMcpVersion = "2.7.0"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

Import-Module (Join-Path $PSScriptRoot 'common.psm1') -Force

$Runtime = Get-ContainerRuntime
$ContextDir = $PSScriptRoot | Split-Path  # scripts/ -> project root

Write-Step "Building image $Image from $ContextDir (runtime: $Runtime)"
& $Runtime build `
    --build-arg "PI_VERSION=$PiVersion" `
    --build-arg "ADO_MCP_VERSION=$AdoMcpVersion" `
    -t $Image $ContextDir

if ($LASTEXITCODE -ne 0) { Fail "Build failed." }
Write-Step "Image $Image built successfully"
