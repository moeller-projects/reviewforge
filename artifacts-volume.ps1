#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Manage the reviewforge-artifacts Podman volume.

.PARAMETER Mode
    mount : export the volume into ./mount via a long-running helper container.
    clean : delete run directories older than -RetentionDays (default 7).

.EXAMPLE
    ./artifacts-volume.ps1 mount
    ./artifacts-volume.ps1 clean -RetentionDays 14
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true, Position = 0)]
    [ValidateSet('mount', 'clean')]
    [string]$Mode,

    [Parameter()]
    [int]$RetentionDays = 7
)

$ErrorActionPreference = 'Stop'
$VolumeName = 'reviewforge-artifacts'

function Invoke-Mount {
    # Replace a stale helper container from a previous run.
    $existing = podman ps -a --filter 'name=^volume-share$' --format '{{.Names}}'
    if ($existing -eq 'volume-share') {
        podman rm -f volume-share | Out-Null
    }

    $mountDir = Join-Path $PSScriptRoot 'mount'
    New-Item -ItemType Directory -Force -Path $mountDir | Out-Null

    podman run -d --name volume-share `
        -v "${VolumeName}:/workspace/artifacts" `
        -v "${mountDir}:/mount" `
        alpine sh -c "cp -a /workspace/artifacts/. /mount/ && tail -f /dev/null" | Out-Null

    $state = podman inspect volume-share --format '{{.State.Status}}'
    if ($state -ne 'running') {
        podman logs volume-share
        throw "volume-share exited during copy (state: $state); see logs above."
    }
    Write-Host "volume-share running; artifacts exported to $mountDir"
    Write-Host "Stop with: podman rm -f volume-share"
}

function Invoke-Clean {
    # Run ids start with a UTC timestamp (%Y%m%dT%H%M%SZ-pid); lexicographic and
    # numeric comparison on the 8-digit date prefix is equivalent. Run ids
    # without a timestamp prefix (e.g. custom REVIEW_RUN_ID) are never deleted.
    $cutoff = (Get-Date).Date.AddDays(-$RetentionDays).ToString('yyyyMMdd')

    $script = @'
set -u
removed=0
for pr in /workspace/artifacts/pr-*/; do
    runs_dir="${pr}runs"
    [ -d "$runs_dir" ] || continue
    for run in "$runs_dir"/*/; do
        [ -d "$run" ] || continue
        name=$(basename "$run")
        case "$name" in
            [0-9][0-9][0-9][0-9][0-9][0-9][0-9][0-9]T*)
                date=${name%%T*}
                if [ "$date" -lt "$CUTOFF" ]; then
                    rm -rf "$run"
                    echo "removed ${run}"
                    removed=$((removed + 1))
                fi
                ;;
        esac
    done
    # latest.txt holds the absolute path of the newest run; repoint or drop it
    # if its target was just deleted.
    latest="${pr}latest.txt"
    if [ -f "$latest" ]; then
        target=$(cat "$latest")
        if [ ! -d "$target" ]; then
            newest=$(ls -1 "$runs_dir" 2>/dev/null | sort | tail -n 1)
            if [ -n "$newest" ]; then
                echo "${runs_dir}/${newest}" > "$latest"
            else
                rm -f "$latest"
            fi
        fi
    fi
    # Prune now-empty runs/ and pr-*/ directories.
    [ -d "$runs_dir" ] && [ -z "$(ls -A "$runs_dir")" ] && rmdir "$runs_dir"
    [ -z "$(ls -A "$pr")" ] && rmdir "$pr"
done
echo "clean complete: $removed run(s) removed (cutoff $CUTOFF)"
'@
    # The container's sh chokes on CRLF; normalise regardless of file encoding.
    $script = $script -replace "`r`n", "`n"

    podman run --rm `
        -e "CUTOFF=$cutoff" `
        -v "${VolumeName}:/workspace/artifacts" `
        alpine sh -c "$script"
}

switch ($Mode) {
    'mount' { Invoke-Mount }
    'clean' { Invoke-Clean }
}
