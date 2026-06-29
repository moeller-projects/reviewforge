<#
.SYNOPSIS
    Register Windows Task Scheduler job for run-open-prs-scheduled.ps1.

.DESCRIPTION
    Creates one scheduled task with two daily triggers by default:
    09:30 and 15:00. Times are configurable.

    The task runs current PowerShell user context and executes the repo
    wrapper script directly.
#>
[CmdletBinding()]
param(
    [string]   $TaskName = "pr-review-bot-open-prs",
    [string]   $ScriptPath = (Join-Path $PSScriptRoot 'run-open-prs-scheduled.ps1'),
    [string[]] $Times = @('09:30', '15:00'),
    [string]   $Description = "Run open PR reviewer twice per day",
    [switch]   $Force,
    [string]   $Organization,
    [string[]] $Projects,
    [string[]] $TargetBranches,
    [string]   $AdoToken,
    [int]      $MaxPullRequests = 0,
    [switch]   $DryRun,
    [switch]   $Build,
    [string]   $EnvFile = ".env",
    [switch]   $KeepContainer,
    [string]   $LogDirectory = (Join-Path $PSScriptRoot 'logs/open-prs')
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

Import-Module (Join-Path $PSScriptRoot 'common.psm1') -Force

$defaultEnvFile = Join-Path $PSScriptRoot '.env'
if (Test-Path -LiteralPath $defaultEnvFile) {
    Import-DotEnvFile -Path $defaultEnvFile | Out-Null
}

if (-not (Test-Path -LiteralPath $ScriptPath)) {
    Fail "Script not found: $ScriptPath"
}

$triggers = foreach ($time in $Times) {
    try {
        $parsed = [TimeSpan]::Parse($time)
    } catch {
        Fail "Invalid time '$time'. Use HH:mm, e.g. 09:30"
    }

    $at = [DateTime]::Today.Add($parsed)
    New-ScheduledTaskTrigger -Daily -At $at
}

$argList = @(
    '-NoProfile'
    '-ExecutionPolicy', 'Bypass'
    '-File', (Resolve-Path -LiteralPath $ScriptPath).ProviderPath
)

if ($Organization)    { $argList += @('-Organization', $Organization) }
if ($Projects)        { $argList += @('-Projects', ($Projects -join ',')) }
if ($TargetBranches)  { $argList += @('-TargetBranches', ($TargetBranches -join ',')) }
if ($AdoToken)        { $argList += @('-AdoToken', $AdoToken) }
if ($MaxPullRequests -gt 0) { $argList += @('-MaxPullRequests', "$MaxPullRequests") }
if ($DryRun)          { $argList += '-DryRun' }
if ($Build)           { $argList += '-Build' }
if ($EnvFile)         { $argList += @('-EnvFile', $EnvFile) }
if ($KeepContainer)   { $argList += '-KeepContainer' }
if ($LogDirectory)    { $argList += @('-LogDirectory', $LogDirectory) }

$action = New-ScheduledTaskAction -Execute "pwsh.exe" -Argument ($argList -join ' ')
$principal = New-ScheduledTaskPrincipal -UserId $env:USERNAME -LogonType Interactive -RunLevel Limited
$settings = New-ScheduledTaskSettingsSet -StartWhenAvailable -MultipleInstances IgnoreNew -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries

$task = New-ScheduledTask -Action $action -Trigger $triggers -Principal $principal -Settings $settings -Description $Description

$existingTask = Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue
if ($existingTask) {
    if (-not $Force) {
        Write-Step "Scheduled task '$TaskName' exists; replacing it"
    }
    Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false
}

Register-ScheduledTask -TaskName $TaskName -InputObject $task | Out-Null
Write-Step "Scheduled task '$TaskName' registered with times: $($Times -join ', ')"
