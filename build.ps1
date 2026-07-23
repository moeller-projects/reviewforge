<# Compatibility wrapper. Prefer `python -m reviewforge.ops build`. #>
[CmdletBinding()]
param([string]$Image, [string]$PiVersion, [string]$EnvFile = ".env")
$ErrorActionPreference = "Stop"
$env:PYTHONPATH = "$PSScriptRoot/src" + $(if ($env:PYTHONPATH) { ";$env:PYTHONPATH" } else { "" })
$args = @("-m", "reviewforge.ops", "build")
if ($Image) { $args += @("--image", $Image) }
if ($PiVersion) { $args += @("--pi-version", $PiVersion) }
& python @args
exit $LASTEXITCODE
