# Operator and scheduling workflows

**Purpose:** run ReviewForge container workflows and locate outputs. **Audience:** operators. **Mode:** how-to.

## Cross-platform entrypoints

Use Python on Linux, macOS, or Windows:

```bash
python -m reviewforge.ops build --dry-run
python -m reviewforge.ops run --dry-run --print-command --env-file .env \
  --pr-url https://dev.azure.com/example/project/_git/repo/pullrequest/1 \
  --ado-token placeholder
python -m reviewforge.ops run-open-prs --organization https://dev.azure.com/example/ \
  --projects project --target-branches main --dry-run
```

`build`, `run`, and `run-open-prs` choose Docker then Podman unless
`--runtime` is supplied. Explicit flags override environment variables, and
the chosen `--env-file` is passed directly to the container. `--print-command`
previews a single-review invocation without spawning a container.

`run-open-prs` keeps batch selection semantics: `--max-pull-requests` caps
the sorted matching set before review, and `--interactive` accepts `all`,
`none`, comma-separated indexes, and inclusive ranges such as `1,3-5`.

## PowerShell compatibility

`build.ps1`, `run.ps1`, and `run-open-prs.ps1` now forward to the Python
entrypoints. They remain for existing Windows operators and scheduled tasks,
but new automation should invoke `python -m reviewforge.ops`.

`setup-open-prs-schedule.ps1` remains Windows Task Scheduler integration; it
continues to invoke the batch compatibility wrapper.

## Artifacts and posting

Review output is written under `REVIEW_ARTIFACT_ROOT/pr-<PR_ID>/runs/<RUN_ID>/`. Read `run.log` there for the chronological, redacted container log for that run; `pr-<PR_ID>/latest.txt` identifies the latest run directory. Preserve `run-summary.json`, `review-result.json`, and `final-findings.json` when diagnosing or reposting. The container volume is already mounted by `run.ps1` and `run-open-prs.ps1`, so the same path is available to PowerShell operators. Do not edit the `prb:` deduplication marker in posted comment bodies; see [ADO integration](../reference/ado-integration.md).
