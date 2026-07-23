<#
.SYNOPSIS
    Scheduled wrapper for run-open-prs.ps1.

.DESCRIPTION
    Thin shim for scheduled runs. Keeps same repo logic as
    run-open-prs.ps1, adds log-friendly timestamped output, and
    defaults to unattended execution.

    Use -Interactive only for manual runs.
#>
[CmdletBinding()]
param(
    [string]   $Organization,
    [string[]] $Projects,
    [string[]] $TargetBranches,
    [string]   $AdoToken,
    [int]      $MaxPullRequests = 0,
    [switch]   $DryRun,
    [switch]   $Build,
    [string]   $EnvFile = ".env",
    [switch]   $Interactive = $false,
    [switch]   $KeepContainer,
    [string]   $LogDirectory = (Join-Path $PSScriptRoot 'logs/open-prs')
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

Import-Module (Join-Path $PSScriptRoot 'common.psm1') -Force

$envFilePath = if ([System.IO.Path]::IsPathRooted($EnvFile)) {
    $EnvFile
} else {
    Join-Path $PSScriptRoot $EnvFile
}
if (Test-Path -LiteralPath $envFilePath) {
    Import-DotEnvFile -Path $envFilePath | Out-Null
}
if ($AdoToken) {
    Write-Warning "-AdoToken was supplied to a scheduled run, but it is never forwarded or logged. Put ADO_AUTH_TOKEN or ADO_API_KEY in the .env file referenced by -EnvFile."
}

if ($LogDirectory) {
    $resolvedLogDir = [System.IO.Path]::GetFullPath($LogDirectory)
    if (-not (Test-Path -LiteralPath $resolvedLogDir)) {
        New-Item -Path $resolvedLogDir -ItemType Directory -Force | Out-Null
    }
    $stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
    $logPath = Join-Path $resolvedLogDir "run-open-prs-$stamp.log"
    Start-Transcript -Path $logPath -Append | Out-Null
    Write-Step "Logging to $logPath"
}

try {
    & (Join-Path $PSScriptRoot 'run-open-prs.ps1') `
        -Organization $Organization `
        -Projects $Projects `
        -TargetBranches $TargetBranches `
        -MaxPullRequests $MaxPullRequests `
        -DryRun:([bool]$DryRun) `
        -Build:([bool]$Build) `
        -EnvFile $EnvFile `
        -Interactive:([bool]$Interactive) `
        -KeepContainer:([bool]$KeepContainer)
    exit $LASTEXITCODE
} finally {
    if ($LogDirectory) {
        Stop-Transcript | Out-Null
    }
}
