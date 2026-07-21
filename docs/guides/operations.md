# Operator and scheduling workflows

**Purpose:** run the checked-in PowerShell workflows and locate outputs. **Audience:** operators deploying ReviewForge on Windows/PowerShell hosts. **Mode:** how-to.

## Single review

`run.ps1` is the repository wrapper for one review. It supplies the container/runtime invocation and forwards the configured Azure DevOps and PR values. Inspect its parameter surface before execution because deployment-specific values are intentionally not duplicated here.

## Batch and scheduled reviews

- `run-open-prs.ps1` processes active pull requests in batch.
- `run-open-prs-scheduled.ps1` is the unattended batch wrapper.
- `setup-open-prs-schedule.ps1` registers the twice-daily Windows Task Scheduler job.

The wrappers require Azure DevOps organization/project/repository credentials and target-branch configuration. Keep the token in the environment or secret store expected by the wrapper; do not commit it. The scheduled wrapper uses its own overlap protection so a second scheduled invocation does not run concurrently with an active batch.

The primary CLI's `open-prs` command is intentionally unsupported for batch execution; use the checked-in PowerShell wrapper instead. See [CLI reference](../reference/cli.md).

## Artifacts and posting

Review output is written under `REVIEW_ARTIFACT_ROOT/pr-<PR_ID>/runs/<RUN_ID>/`. Preserve `run-summary.json`, `review-result.json`, and `final-findings.json` when diagnosing or reposting. Do not edit the `prb:` deduplication marker in posted comment bodies; see [ADO integration](../reference/ado-integration.md).
