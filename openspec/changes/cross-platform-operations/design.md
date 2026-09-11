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


## Follow-up behavior decisions

- Interactive selectors resolve exact displayed PR IDs in addition to positional indexes and index ranges. Exact ID matches take precedence for a numeric token; unresolved values retain index-range validation and produce a clear ID-not-found error when outside the displayed index set.
- Container execution remains detached for all runs. `--keep-container` changes cleanup only: it omits `--rm` and retains `-d`.
- `run-open-prs` uses the existing `reviewforge discover` CLI subprocess, preserving its authentication and filtering behavior.
