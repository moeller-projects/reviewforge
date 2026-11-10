#Requires -Version 7.0
<#
.SYNOPSIS
    Provisions the identity ReviewForge uses in Azure DevOps.

.DESCRIPTION
    1. Signs in to your Entra tenant with the Azure CLI
    2. Creates the bot user in Entra ID (skipped if it already exists)
    3. Adds the user to your Azure DevOps organization (Basic license)
    4. For EVERY project: adds the bot to the "Readers" group and grants it on all repos
       Read + Contribute + Contribute to pull requests
       (NOT granted: force push, bypass policies, manage permissions)
    5. Optionally creates an organization-scoped PAT (you sign in as the bot once)

    Prerequisites: PowerShell 7+, Azure CLI, and an account that is
    User Administrator in Entra and Project Collection Administrator in the DevOps org.

    Run with $DryRun = $true first (default). Nothing is changed in dry-run mode.
#>

$ErrorActionPreference = 'Stop'

# =====================================================================
# CONFIGURATION: replace the placeholders
# =====================================================================
$TenantId        = '<TENANT_ID>'            # Entra tenant GUID
$OrgName         = '<ORG_NAME>'             # https://dev.azure.com/<ORG_NAME>
$BotDisplayName  = '<BOT_DISPLAY_NAME>'     # shown on PR comments, e.g. 'Dev Bot'
$BotUpn          = '<BOT_EMAIL>'            # e.g. 'dev-bot@yourdomain.com'

$LicenseType     = 'express'                # express = Basic
$InitialPassword = ''                       # empty = generate a random one
$DryRun          = $true                    # set to $false to apply changes

$CreatePat       = $false                   # $true = also create the PAT (see step 5)
$PatName         = 'reviewforge'
$PatLifetimeDays = 180                      # max 365
$PatScopes       = 'vso.code_write vso.threads_full vso.work'
                                            # Code (Read & write), PR Threads (Read & write), Work Items (Read)
# =====================================================================

# Constants (Git Repositories security namespace)
$RepoNamespaceId          = '2e9eb7ed-3c0a-47d4-87c1-0ffdd275fd87'
$BitRead                  = 2
$BitContribute            = 4
$BitPullRequestContribute = 16384
$AllowBits                = $BitRead + $BitContribute + $BitPullRequestContribute   # 16390
$AzureDevOpsResourceId    = '499b84ac-1321-427f-aa17-267ca6975798'

$OrgUrl       = "https://dev.azure.com/$OrgName"
$MailNickname = ($BotUpn -split '@')[0]

# ---------------------------------------------------------------------
# Helpers
# ---------------------------------------------------------------------
function Write-Step([string]$Text) { Write-Host "`n==> $Text" -ForegroundColor Cyan }

function Format-AzArgs([string[]]$AzArgs) {
    $masked = for ($i = 0; $i -lt $AzArgs.Count; $i++) {
        if ($i -gt 0 -and $AzArgs[$i - 1] -eq '--password') { '********' } else { $AzArgs[$i] }
    }
    'az ' + ($masked -join ' ')
}

function Invoke-Az {
    param(
        [Parameter(Mandatory)][string[]]$AzArgs,
        [switch]$Mutating,
        [switch]$AsJson
    )
    if ($Mutating -and $DryRun) {
        Write-Host "  [dry-run] $(Format-AzArgs $AzArgs)" -ForegroundColor DarkGray
        return $null
    }
    $out = & az @AzArgs 2>&1
    if ($LASTEXITCODE -ne 0) {
        $errText = (($out | ForEach-Object { "$_".Trim() } | Where-Object { $_ }) -join ' ')
        throw "az $($AzArgs[0..([Math]::Min(3, $AzArgs.Count - 1))] -join ' ') failed: $errText"
    }
    if ($AsJson) {
        $text = ($out | Where-Object { $_ -isnot [System.Management.Automation.ErrorRecord] }) -join "`n"
        if ([string]::IsNullOrWhiteSpace($text)) { return $null }
        return $text | ConvertFrom-Json
    }
    return $out
}

function New-StrongPassword([int]$Length = 24) {
    # Avoid characters that cmd.exe/az.cmd may mangle (% ^ ! & | < >)
    $sets = @('ABCDEFGHJKLMNPQRSTUVWXYZ', 'abcdefghijkmnopqrstuvwxyz', '23456789', '-_.+')
    $all  = -join $sets
    $rng  = [System.Security.Cryptography.RandomNumberGenerator]
    $chars = [System.Collections.Generic.List[char]]::new()
    foreach ($s in $sets) { $chars.Add($s[$rng::GetInt32($s.Length)]) }
    while ($chars.Count -lt $Length) { $chars.Add($all[$rng::GetInt32($all.Length)]) }
    $arr = $chars.ToArray()
    for ($i = $arr.Length - 1; $i -gt 0; $i--) {
        $j = $rng::GetInt32($i + 1)
        $arr[$i], $arr[$j] = $arr[$j], $arr[$i]
    }
    -join $arr
}

# ---------------------------------------------------------------------
# Step 0: validate configuration
# ---------------------------------------------------------------------
Write-Step 'Validating configuration'
foreach ($name in 'TenantId', 'OrgName', 'BotDisplayName', 'BotUpn') {
    $value = (Get-Variable $name).Value
    if ([string]::IsNullOrWhiteSpace($value) -or $value -match '<.*>') {
        throw "Set `$$name at the top of the script (still a placeholder)."
    }
}
if ($BotUpn -notmatch '^[^@\s]+@[^@\s]+\.[^@\s]+$') { throw "`$BotUpn is not a valid email/UPN: $BotUpn" }
if (-not (Get-Command az -ErrorAction SilentlyContinue)) { throw 'Azure CLI (az) not found in PATH.' }
if ($DryRun) { Write-Host 'DRY RUN: nothing will be changed. Set $DryRun = $false to apply.' -ForegroundColor Yellow }

# ---------------------------------------------------------------------
# Step 1: sign in to the tenant, make sure the DevOps extension exists
# ---------------------------------------------------------------------
Write-Step "Signing in to tenant $TenantId"
$account = $null
try { $account = Invoke-Az -AsJson -AzArgs @('account', 'show') } catch { }
if (-not $account -or $account.tenantId -ne $TenantId) {
    Invoke-Az -AzArgs @('login', '--tenant', $TenantId, '--allow-no-subscriptions') | Out-Null
    $account = Invoke-Az -AsJson -AzArgs @('account', 'show')
}
Write-Host "  signed in as $($account.user.name)"

$hasExt = $false
try { Invoke-Az -AzArgs @('extension', 'show', '--name', 'azure-devops') | Out-Null; $hasExt = $true } catch { }
if (-not $hasExt) { Invoke-Az -AzArgs @('extension', 'add', '--name', 'azure-devops', '--yes') | Out-Null }

# ---------------------------------------------------------------------
# Step 2: create the Entra user
# ---------------------------------------------------------------------
Write-Step "Entra user $BotUpn"
$entraUser = $null
try { $entraUser = Invoke-Az -AsJson -AzArgs @('ad', 'user', 'show', '--id', $BotUpn) } catch { }

if ($entraUser) {
    Write-Host '  already exists, skipping creation'
} else {
    $password = if ([string]::IsNullOrWhiteSpace($InitialPassword)) { New-StrongPassword } else { $InitialPassword }
    Invoke-Az -Mutating -AzArgs @(
        'ad', 'user', 'create',
        '--display-name', $BotDisplayName,
        '--user-principal-name', $BotUpn,
        '--mail-nickname', $MailNickname,
        '--password', $password,
        '--force-change-password-next-sign-in', 'true'
    ) | Out-Null
    if (-not $DryRun) {
        Write-Host '  created'
        Write-Host "  Temporary password (shown once, must be changed at first sign-in): $password" -ForegroundColor Yellow
    }
}

# ---------------------------------------------------------------------
# Step 3: add the user to the Azure DevOps organization
# ---------------------------------------------------------------------
Write-Step "Azure DevOps organization $OrgUrl"
$orgUser = $null
try { $orgUser = Invoke-Az -AsJson -AzArgs @('devops', 'user', 'show', '--user', $BotUpn, '--org', $OrgUrl) } catch { }

if ($orgUser) {
    Write-Host '  already a member of the organization'
} else {
    for ($attempt = 1; $attempt -le 6; $attempt++) {
        try {
            Invoke-Az -Mutating -AzArgs @(
                'devops', 'user', 'add',
                '--email-id', $BotUpn,
                '--license-type', $LicenseType,
                '--send-email-invite', 'false',
                '--org', $OrgUrl
            ) | Out-Null
            break
        } catch {
            if ($attempt -eq 6) { throw }
            Write-Warning "Attempt $attempt failed (Entra replication delay?). Retrying in 15 s."
            Start-Sleep -Seconds 15
        }
    }
}

# ---------------------------------------------------------------------
# Step 4: all projects: Readers membership + repo permissions
# ---------------------------------------------------------------------
Write-Step 'Granting access in all projects'
$projects = @()
$skip = 0
do {
    $page  = Invoke-Az -AsJson -AzArgs @('devops', 'project', 'list', '--org', $OrgUrl, '--top', '100', '--skip', "$skip")
    $batch = @($page.value)
    $projects += $batch
    $skip += 100
} while ($batch.Count -eq 100)
Write-Host "  found $($projects.Count) project(s)"

# Groups the bot is already in (best effort; used only to skip redundant adds).
$memberOfJson = $null
try {
    $memberOf = Invoke-Az -AsJson -AzArgs @('devops', 'security', 'group', 'membership', 'list',
        '--id', $BotUpn, '--relationship', 'memberof', '--org', $OrgUrl)
    if ($memberOf) { $memberOfJson = $memberOf | ConvertTo-Json -Depth 10 -Compress }
} catch { }

$results = foreach ($p in $projects) {
    $status = 'ok'
    try {
        $groups  = Invoke-Az -AsJson -AzArgs @('devops', 'security', 'group', 'list', '--project', $p.id, '--scope', 'project', '--org', $OrgUrl)
        $readers = @($groups.graphGroups) | Where-Object { $_.displayName -eq 'Readers' } | Select-Object -First 1
        if (-not $readers) { throw 'Readers group not found' }

        $alreadyMember = $memberOfJson -and $memberOfJson.Contains($readers.descriptor)
        if (-not $alreadyMember) {
            try {
                Invoke-Az -Mutating -AzArgs @('devops', 'security', 'group', 'membership', 'add',
                    '--group-id', $readers.descriptor, '--member-id', $BotUpn, '--org', $OrgUrl) | Out-Null
            } catch {
                if ($_.Exception.Message -notmatch 'already|exists') { throw }
            }
        }

        Invoke-Az -Mutating -AzArgs @('devops', 'security', 'permission', 'update',
            '--id', $RepoNamespaceId, '--subject', $BotUpn,
            '--token', "repoV2/$($p.id)", '--allow-bit', "$AllowBits", '--merge', 'true',
            '--org', $OrgUrl) | Out-Null
    } catch {
        $msg = $_.Exception.Message.Split("`n")[0]
        $status = 'FAILED: ' + $(if ($msg.Length -gt 140) { $msg.Substring(0, 140) + '...' } else { $msg })
    }
    [pscustomobject]@{ Project = $p.name; Status = $status }
}
Write-Host (($results | Format-Table -AutoSize -Wrap | Out-String -Width 200).TrimEnd())
$failed = @($results | Where-Object { $_.Status -ne 'ok' })
if ($failed.Count -gt 0) {
    Write-Warning "$($failed.Count) project(s) failed. Fix the cause (see Status) and re-run; the script is idempotent."
}

# ---------------------------------------------------------------------
# Step 5: optional PAT (the bot must sign in itself; PATs cannot be created for other users)
# ---------------------------------------------------------------------
$pat = $null
if ($CreatePat -and -not $DryRun) {
  try {
    Write-Step "Creating an organization-scoped PAT as $BotUpn"
    Write-Host "  A browser window opens. Sign in AS THE BOT ($BotUpn); use a private window or 'use another account'." -ForegroundColor Yellow
    Write-Host '  You will be asked to change the temporary password.' -ForegroundColor Yellow
    Invoke-Az -AzArgs @('login', '--tenant', $TenantId, '--allow-no-subscriptions') | Out-Null

    $who = Invoke-Az -AsJson -AzArgs @('account', 'show')
    if ($who.user.name -ne $BotUpn) { throw "Signed in as $($who.user.name), expected $BotUpn. Aborting PAT creation." }

    $token = (Invoke-Az -AsJson -AzArgs @('account', 'get-access-token', '--resource', $AzureDevOpsResourceId)).accessToken
    $body = @{
        displayName = $PatName
        scope       = $PatScopes
        validTo     = (Get-Date).AddDays($PatLifetimeDays).ToUniversalTime().ToString('o')
        allOrgs     = $false
    } | ConvertTo-Json

    $resp = Invoke-RestMethod -Method Post `
        -Uri "https://vssps.dev.azure.com/$OrgName/_apis/tokens/pats?api-version=7.1-preview.1" `
        -Headers @{ Authorization = "Bearer $token" } `
        -ContentType 'application/json' -Body $body
    $pat = $resp.patToken.token
    if ($pat) { $env:REVIEWFORGE_ADO_PAT = $pat }   # this session only
  } catch {
    Write-Warning "PAT creation failed: $($_.Exception.Message.TrimEnd('.')). Create it manually (steps below)."
  }
}

# ---------------------------------------------------------------------
# Summary
# ---------------------------------------------------------------------
Write-Step 'Done'
if ($DryRun) {
    Write-Host 'Dry run finished. Review the output, then set $DryRun = $false and run again.' -ForegroundColor Yellow
    return
}

if ($pat) {
    Write-Host "`nPAT (shown once). Store it in your secret store; it is also set as `$env:REVIEWFORGE_ADO_PAT for this session:" -ForegroundColor Yellow
    Write-Host "  $pat"
} else {
    Write-Host @"

Create the PAT manually:
  1. Sign in at $OrgUrl as $BotUpn (private window).
  2. User settings > Personal access tokens > New Token
  3. Organization: $OrgName (NOT 'All accessible organizations'; global PATs stop working on 2026-12-01)
  4. Scopes: Code (Read & write), Pull Request Threads (Read & write), Work Items (Read)
"@
}

Write-Host @"

ReviewForge environment:
  REVIEWFORGE_ADO_PAT=<the PAT>
  Ado__OrgUrl=$OrgUrl
  Ado__Project=<project>
  AutoFix__PublishMode=CommitOnHead      # enables pushing; default is suggestions only

Reminders:
  - Keep branch policies on main/release branches so the bot can only push to feature branches.
  - Re-run this script (it is idempotent) after creating new projects.
"@

if ($failed.Count -gt 0) { exit 1 }