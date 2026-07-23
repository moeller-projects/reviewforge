## Context

Docker cannot evaluate an env-format file in an ARG default at parse time. The Python build entrypoint therefore supplies both required ARG values and Docker rejects missing values, avoiding duplicate defaults.

## Goals / Non-Goals

**Goals:** share pins, support Docker or Podman from Python, and retain PowerShell invocation compatibility.

**Non-Goals:** replace Windows Task Scheduler or alter Azure pipeline parameters.

## Decisions

- Use stdlib-only `reviewforge.ops` with `python -m reviewforge.ops`.
- Use `versions.env` as the sole default-pin source; Docker ARGs are required inputs validated during build.
- Use a GitHub Actions lint job to compare Azure YAML literals against the pin file.

## Risks / Trade-offs

- `run-open-prs` uses the existing `reviewforge discover` CLI subprocess, preserving its authentication and filtering behavior.
