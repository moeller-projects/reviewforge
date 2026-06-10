<#
.SYNOPSIS
    Build the PR review bot container image.

.DESCRIPTION
    Standalone build script for the pr-review-bot image. Separated from the run
    script so you can build once and run many times without rebuilding.

.PARAMETER Image
    Docker/Podman image tag. Default: pr-review-bot:latest.

.PARAMETER PiVersion
    Pin the @earendil-works/pi-coding-agent version. Default: latest.

.PARAMETER AdoMcpVersion
    Pin the @azure-devops/mcp version. Default: latest.

.EXAMPLE
    ./scripts/build.ps1
    ./scripts/build.ps1 -Image pr-review-bot:v1.2
#>
[CmdletBinding()]
param(
    [string] $Image         = "pr-review-bot:latest",
    [string] $PiVersion     = "latest",
    [string] $AdoMcpVersion = "latest"
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
