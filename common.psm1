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
    Detect the container runtime (docker or podman).
#>
function Get-ContainerRuntime {
    if (Get-Command docker -ErrorAction SilentlyContinue) { return "docker" }
    if (Get-Command podman -ErrorAction SilentlyContinue) { return "podman" }
    Fail "Neither podman nor docker found on PATH."
}

<#
.SYNOPSIS
    Load KEY=VALUE pairs from a ``.env`` file into the current process
    environment. Existing process env vars always win. Lines that are
    blank, start with ``#``, or do not match ``KEY=VALUE`` are silently
    ignored. Quoted values are NOT processed (kept verbatim including
    the quotes); users can write ``ADO_AUTH_TOKEN=xyz`` without quoting
    and rely on the Python side to consume the value as-is.

    This helper exists for the PowerShell wrappers only so they can
    populate the temp env-file used by ``docker run --env-file``. The
    canonical .env parser lives in ``auto_pr_reviewer.config.parse_dotenv``
    for callers that invoke the Python CLI directly.
#>
function Import-DotEnv {
    param([string]$Path)
    if (-not $Path) { return }
    if (-not (Test-Path -LiteralPath $Path)) { return }
    $resolved = (Resolve-Path -LiteralPath $Path).ProviderPath
    foreach ($line in [System.IO.File]::ReadLines($resolved)) {
        $trimmed = $line.Trim()
        if (-not $trimmed -or $trimmed.StartsWith('#')) { continue }
        if ($trimmed -notmatch '^([A-Za-z_][A-Za-z0-9_]*)=(.*)$') { continue }
        $key = $Matches[1]
        if ([System.Environment]::GetEnvironmentVariable($key, 'Process')) { continue }
        [System.Environment]::SetEnvironmentVariable($key, $Matches[2], 'Process')
    }
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

Export-ModuleMember -Function Write-Step, Fail, Get-ContainerRuntime, Import-DotEnv, Write-EnvFile, Get-ReviewerEnvFile
