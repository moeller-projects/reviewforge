<# Compatibility wrapper. Prefer `python -m reviewforge.ops build`. #>
[CmdletBinding()]
param([string]$Image, [string]$PiVersion, [string]$EnvFile = ".env")
$ErrorActionPreference = "Stop"
$env:PYTHONPATH = "$PSScriptRoot/src" + $(if ($env:PYTHONPATH) { "$([IO.Path]::PathSeparator)$env:PYTHONPATH" } else { "" })
$args = @("-m", "reviewforge.ops", "build")
if ($Image) { $args += @("--image", $Image) }
if ($PiVersion) { $args += @("--pi-version", $PiVersion) }
if (Get-Command uv -ErrorAction SilentlyContinue) {
    & uv run python @args
} else {
    & python @args
}
exit $LASTEXITCODE
