# Shared functions for pr-review-bot PowerShell scripts.
#
# This module is a *thin wrapper utility* only. All ADO logic (REST,
# pagination, branch resolution, reviewer lookup, etc.) lives in the
# Python package ``auto_pr_reviewer`` and is executed inside the
# container. The PowerShell scripts just orchestrate the Docker /
# local Python invocation and forward environment variables to the
# container.
#
# Import: Import-Module (Join-Path $PSScriptRoot 'common.psm1') -Force

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Write-Step {
    param([string]$Message)
    Write-Host "==> $Message" -ForegroundColor Cyan
}

function Fail {
    param([string]$Message)
    Write-Host "ERROR: $Message" -ForegroundColor Red
    exit 1
}

<#
.SYNOPSIS
    Look up a process-env variable and fall back to a default when
    the variable is unset, empty, or whitespace-only. Used by the
    wrapper scripts to collapse the ``if (-not $PSBoundParameters...
    -and $env:...)`` preamble into a single call.

.PARAMETER Name
    The environment variable name (case-insensitive on Windows).

.PARAMETER Default
    Value returned when the env var is unset / empty. Pass ``$null``
    to allow nulls through.
#>
function Get-EnvOrDefault {
    param(
        [Parameter(Mandatory)][string]$Name,
        $Default
    )
    $value = [System.Environment]::GetEnvironmentVariable($Name, 'Process')
    if ([string]::IsNullOrWhiteSpace($value)) { return $Default }
    return $value
}

<#
.SYNOPSIS
    Convert a comma-separated string (or already-array) into a
    trimmed, non-empty, deduplicated array. Empty input yields
    ``@()``. Used by callers that accept either
    ``$env:ADO_PROJECTS = "a,b,c"`` or ``-Projects a,b,c``.

.PARAMETER Value
    String (comma-separated) or array. Anything else is passed
    through as-is wrapped in a single-element array.
#>
function ConvertFrom-CommaList {
    param([Parameter(Mandatory)]$Value)
    if ($null -eq $Value) { return @() }
    if ($Value -is [array]) {
        $strings = $Value
    } else {
        $strings = ([string]$Value) -split ','
    }
    return @(
        $strings |
            ForEach-Object { if ($_ -is [string]) { $_.Trim() } else { "$_".Trim() } } |
            Where-Object { $_ }
    ) | Select-Object -Unique
}

<#
.SYNOPSIS
    Snapshot every config var the wrapper scripts care about into a
    single hashtable by reading the live process environment, then
    layer explicit ``-Parameters`` overrides on top. Eliminates the
    repeated 20-line ``if (-not $PSBoundParameters...`` preamble
    that used to live in ``run.ps1``.

.PARAMETER Required
    Array of env-var names that MUST be set after loading. The
    function fails with a clear "set X in the environment or pass
    -X" message listing all missing keys.

.DESCRIPTION
    The returned hashtable is the authoritative source of config
    for the script. Callers pass it to ``Get-ReviewerEnvFile`` and
    to the per-PR ``-e`` overrides. This is the single seam where
    "config comes from somewhere" is decided: precedence is
    explicit -Parameters > process env > defaults baked into the
    called function.

    This function does NOT load ``.env`` files. The ``.env`` file at
    the repo root is a reference / template (see ``.env.example``)
    that documents the available env vars and their typical values.
    The user is responsible for loading it into the process env
    themselves (e.g. via direnv, ``set -a; source .env; set +a`` in
    bash, or manual exports). Once loaded, the vars are visible here
    via ``GetEnvironmentVariable`` exactly like any other env var.
    This keeps precedence predictable and matches the spirit of
    12-factor / docker ``--env-file`` (the file is data, not policy).
#>
function Resolve-ScriptConfig {
    param(
        [hashtable] $Parameters = @{},
        [string[]] $Required = @()
    )

    # Env-var name -> default. Wrappers pass $Parameters (the
    # CmdletBinding param table) for explicit overrides; defaults
    # below are the last-resort values. The Python package owns the
    # authoritative resolution; this table exists so the wrapper
    # doesn't have to know the full env-var list out-of-band.
    $envKeys = @(
        'PR_URL', 'ADO_ORGANIZATION', 'ADO_PROJECTS', 'ADO_TARGET_BRANCHES',
        'ADO_ORG', 'ADO_PROJECT', 'ADO_REPO_ID', 'PR_ID',
        'SOURCE_BRANCH', 'TARGET_BRANCH', 'ADO_AUTH_TOKEN', 'ADO_API_KEY',
        'OPENAI_API_KEY', 'PI_MODEL', 'REVIEW_LANGUAGE', 'FAIL_ON',
        'VOTE_WAITING_ON', 'DRY_RUN', 'IMAGE_NAME', 'IMAGE', 'PI_VERSION',
        'CONTAINER_NAME', 'ARTIFACT_PATH', 'POST_MIN_SEVERITY'
    )

    $cfg = @{}
    foreach ($key in $envKeys) {
        $envValue = [System.Environment]::GetEnvironmentVariable($key, 'Process')
        $cfg[$key] = if ([string]::IsNullOrWhiteSpace($envValue)) { $null } else { $envValue }
    }

    # Explicit caller params win over process env. Caller passes
    # the $PSBoundParameters table so the wrapper doesn't have to
    # hand-roll every override.
    foreach ($name in $Parameters.Keys) {
        $value = $Parameters[$name]
        if ($null -ne $value -and "$value" -ne '') {
            $cfg[$name] = "$value"
        }
    }

    # Validate required keys.
    $missing = @()
    foreach ($req in $Required) {
        if (-not $cfg.ContainsKey($req) -or [string]::IsNullOrWhiteSpace($cfg[$req])) {
            $missing += $req
        }
    }
    if ($missing.Count -gt 0) {
        $hint = ($missing | ForEach-Object { "  - $_ (set in .env or pass -$_)" }) -join "`n"
        Fail "Missing required configuration:`n$hint"
    }

    return $cfg
}

<#
.SYNOPSIS
    Display a numbered list and parse a user selection string
    (e.g. ``1,3-5``, ``all``, ``a``, ``none``, ``n``) into the
    selected indices. Used by ``run-open-prs.ps1`` interactive
    mode. Pure function, no I/O outside the prompt itself; the
    caller passes a Read-Host hook so it's testable.

.PARAMETER Items
    Array of objects to display. Each item is rendered using
    ``Format-ListItem`` below (or a ``ToString()`` fallback).

.PARAMETER Prompt
    Prompt text shown before the input cursor.

.PARAMETER Read-HostHook
    ScriptBlock that returns the user input. Defaults to
    ``{ Read-Host -Prompt $Prompt }``. Tests pass a mock.

.DESCRIPTION
    Returns a sorted, unique array of selected indices (1-based,
    matching what the user sees). Invalid input re-prompts until
    the user types a valid selection or cancels. Returns ``@()``
    on cancel / ``none``.

    Supported syntax:
      * ``all`` / ``a``     -> all indices
      * ``none`` / ``n``    -> empty array (caller treats as
                                "user opted out", exits 0)
      * ``1,3,5``           -> literal indices
      * ``3-5``             -> inclusive range
      * ``1,3-5,7``         -> mixed
      * empty input         -> re-prompt
#>
function Show-InteractivePrompt {
    param(
        [Parameter(Mandatory)][object[]]$Items,
        [Parameter(Mandatory)][string]$Prompt,
        [scriptblock] $ReadHostHook = { param($p) Read-Host -Prompt $p }
    )

    if ($Items.Count -eq 0) { return ,@() }

    Write-Host $Prompt -NoNewline
    Write-Host ""

    for ($i = 0; $i -lt $Items.Count; $i++) {
        $label = $Items[$i] | Out-String
        $label = ($label -split "`r?`n")[0].TrimEnd()
        Write-Host ("  [{0,2}] {1}" -f ($i + 1), $label)
    }
    Write-Host "  [all] review all  |  [none] cancel"
    Write-Host ""

    while ($true) {
        $raw = & $ReadHostHook "Selection: "
        if ($null -eq $raw) { return ,@() }
        $raw = "$raw".Trim().ToLowerInvariant()
        if ($raw -in @('all', 'a')) {
            # Unary comma keeps the array intact across the function
            # boundary; without it, a 0/1-element return is unrolled by
            # the pipeline and the caller's ``.Count`` throws under
            # Set-StrictMode.
            return ,(1..$Items.Count)
        }
        if ($raw -in @('none', 'n', '')) {
            if ($raw -eq '') { continue }   # re-prompt on empty
            return ,@()
        }
        # Parse ``1,3-5`` syntax.
        $selected = New-Object System.Collections.Generic.List[int]
        $ok = $true
        foreach ($piece in ($raw -split ',')) {
            $piece = $piece.Trim()
            if ($piece -match '^(\d+)-(\d+)$') {
                $a = [int]$Matches[1]; $b = [int]$Matches[2]
                if ($a -gt $b) { $a, $b = $b, $a }
                for ($k = $a; $k -le $b; $k++) {
                    if ($k -lt 1 -or $k -gt $Items.Count) { $ok = $false; break }
                    $selected.Add($k) | Out-Null
                }
            } elseif ($piece -match '^(\d+)$') {
                $k = [int]$Matches[1]
                if ($k -lt 1 -or $k -gt $Items.Count) { $ok = $false; break }
                $selected.Add($k) | Out-Null
            } else {
                $ok = $false; break
            }
            if (-not $ok) { break }
        }
        if ($ok) {
            # See note above: unary comma preserves the array shape
            # for single-item selections (e.g. user typed "3").
            return ,@($selected | Sort-Object -Unique)
        }
        Write-Host "  Invalid selection. Use 1,3-5, 'all', or 'none'." -ForegroundColor Yellow
    }
}

<#
.SYNOPSIS
    Detect the container runtime (docker or podman).
#>
function Get-ContainerRuntime {
    if (Get-Command docker -ErrorAction SilentlyContinue) { return "docker" }
    if (Get-Command podman -ErrorAction SilentlyContinue) { return "podman" }
    Fail "Neither podman nor docker found on PATH."
}

<#
.SYNOPSIS
    Write the env file for the container and return its path.

    Pure string concatenation. No parsing, no normalization, no token
    resolution. The caller is responsible for ensuring each value is
    safe to write to disk. The temp directory is process-private on
    Windows; on Linux, ``mktemp -d`` would be more secure but the test
    container uses a single-user mount.

    The caller MUST delete the file in a ``finally`` block.
#>
function Write-EnvFile {
    param(
        [Parameter(Mandatory)][hashtable]$Vars
    )

    $envFile = Join-Path ([System.IO.Path]::GetTempPath()) ("pr-review-bot-{0}.env" -f ([guid]::NewGuid()))
    $sb = [System.Text.StringBuilder]::new()
    foreach ($key in $Vars.Keys) {
        $value = $Vars[$key]
        if ($null -eq $value) { continue }
        [void]$sb.AppendLine(("{0}={1}" -f $key, $value))
    }
    [System.IO.File]::WriteAllText(
        $envFile,
        $sb.ToString(),
        (New-Object System.Text.UTF8Encoding($false))
    )
    return $envFile
}

<#
.SYNOPSIS
    Decide which env file to pass to ``docker run --env-file``.

    When a ``.env`` file exists (and ``$EnvFile`` points to it), return
    that path verbatim so Docker's native ``--env-file`` behavior takes
    over. This is the default path: ``run.ps1 -EnvFile .env`` (or unset)
    uses the file directly without copying it anywhere.

    When no ``.env`` file is available, fall back to building a temp
    env file from the current process env so callers that set vars in
    their shell (e.g. ``$env:ADO_AUTH_TOKEN=...``) still reach the
    container. The caller MUST delete the file in a ``finally`` block
    when ``IsTemp`` is ``$true``.

    Returns a ``[pscustomobject]`` with:
      * ``Path``   - absolute path to the env file to pass to Docker.
      * ``IsTemp`` - ``$true`` iff the path is a temp file the caller
                     created and must clean up.

.DESCRIPTION
    The contract is intentionally simple: callers always pass the
    returned ``Path`` to ``--env-file``, and they only do cleanup
    bookkeeping when ``IsTemp`` is true. This keeps the call site
    uniform regardless of whether the user has a ``.env`` file.
#>
function Import-DotEnvFile {
    param(
        [Parameter(Mandatory)][string]$Path,
        [switch]$OverrideExisting
    )

    if (-not (Test-Path -LiteralPath $Path)) { return $false }
    Get-Content -LiteralPath $Path | ForEach-Object {
        $line = $_.Trim()
        if (-not $line -or $line.StartsWith('#')) { return }
        if ($line -notmatch '^(?<key>[A-Za-z_][A-Za-z0-9_]*)=(?<value>.*)$') { return }
        $key = $Matches['key']
        $value = $Matches['value']
        if ($OverrideExisting -or [string]::IsNullOrWhiteSpace([System.Environment]::GetEnvironmentVariable($key, 'Process'))) {
            [System.Environment]::SetEnvironmentVariable($key, $value, 'Process')
        }
    }
    return $true
}

function Get-ReviewerEnvFile {
    param(
        [string] $EnvFile = ".env",
        [hashtable] $ProcessEnvSnapshot
    )

    # When the caller passes a real .env file path, use it as-is. Docker
    # reads it line-by-line and a later ``-e KEY=val`` override beats it.
    if ($EnvFile -and (Test-Path -LiteralPath $EnvFile)) {
        return [pscustomobject]@{
            Path   = (Resolve-Path -LiteralPath $EnvFile).ProviderPath
            IsTemp = $false
        }
    }

    # No .env: build a temp file from the process env so shell-set
    # variables still reach the container. Caller cleans up.
    if (-not $ProcessEnvSnapshot) { $ProcessEnvSnapshot = @{} }
    $tempPath = Write-EnvFile -Vars $ProcessEnvSnapshot
    return [pscustomobject]@{
        Path   = $tempPath
        IsTemp = $true
    }
}

Export-ModuleMember -Function Write-Step, Fail, Get-ContainerRuntime, Write-EnvFile, Get-ReviewerEnvFile, Import-DotEnvFile, Get-EnvOrDefault, Resolve-ScriptConfig, ConvertFrom-CommaList, Show-InteractivePrompt
