# `docs/archive/` — migration, triage, and historical notes

These docs describe **decisions already made** (or already superseded). They
are kept for context only and do not reflect the current system.

| Doc | Why it is archived |
| --- | --- |
| [`python-ado-review-migration.md`](python-ado-review-migration.md) | Plan and migration notes for the move from the PowerShell-only `ado_review.ps1` script to the `auto_pr_reviewer` Python package. The migration is complete; this is the record. |
| [`ado-integration-triage.md`](ado-integration-triage.md) | Triage notes from the ADO integration rewrite. Useful for understanding why certain decisions were made, but not a how-to. |
| [`semantic-diffing-plan.md`](semantic-diffing-plan.md) | Forward-looking plan for semantic diffing. Not implemented in the current system. |
| [`production-review-workflow.md`](production-review-workflow.md) | Production workflow notes captured during initial rollout. Superseded by the in-repo runbooks. |

For docs that describe how the system works **today**, see
[`../reference/README.md`](../reference/README.md) and
[`../architecture/overview.md`](../architecture/overview.md). Historical
records in this directory are context only, not current operational guidance.
