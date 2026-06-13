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

.EXAMPLE
    ./build.ps1
    ./build.ps1 -Image pr-review-bot:v1.2
#>
[CmdletBinding()]
param(
    [string] $Image,
    [string] $PiVersion,
    [string] $EnvFile = ".env"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

Import-Module (Join-Path $PSScriptRoot 'common.psm1') -Force

if ($EnvFile -and (Test-Path -LiteralPath $EnvFile)) { Import-DotEnv -Path $EnvFile }
if (-not $PSBoundParameters.ContainsKey('Image')) { $Image = $(if ($env:IMAGE_NAME) { $env:IMAGE_NAME } elseif ($env:IMAGE) { $env:IMAGE } else { "pr-review-bot:latest" }) }
if (-not $PSBoundParameters.ContainsKey('PiVersion')) { $PiVersion = $(if ($env:PI_VERSION) { $env:PI_VERSION } else { "0.79.1" }) }

$Runtime = Get-ContainerRuntime
$ContextDir = $PSScriptRoot

Write-Step "Building image $Image from $ContextDir (runtime: $Runtime)"
& $Runtime build `
    --build-arg "PI_VERSION=$PiVersion" `
    -t $Image $ContextDir

if ($LASTEXITCODE -ne 0) { Fail "Build failed." }
Write-Step "Image $Image built successfully"
