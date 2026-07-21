# `docs/reference/` — current implemented behavior

These docs describe what `reviewforge` does **today**. They are kept in
sync with the code; if you change a public interface, update the matching doc
in the same PR.

| Doc | Covers |
| --- | --- |
| [`package-guide.md`](package-guide.md) | Start-here index for the `reviewforge` package: layout, audiences, and pointers to the right deep-dive. |
| [`cli.md`](cli.md) | Subcommands (`review`, `post`, `open-prs`, `validate-config`, `discover`), flags, exit codes. |
| [`configuration.md`](configuration.md) | `Config` dataclass, env-var precedence, alias map, `.env` loading. |
| [`ado-integration.md`](ado-integration.md) | `AdoClient` REST wrapper, idempotent posting (`dedupe_key`, `existing_bot_markers`), diff → `threadContext` mapping, legacy shim. |
| [`pipeline.md`](pipeline.md) | The four-stage production pipeline, engine boundary, compatibility projections, and fallback behavior. |
| [`ai-runner.md`](ai-runner.md) | `PiRunner` subprocess wrapper, session reuse, JSON repair, prompt assembly. |
| [`artifacts.md`](artifacts.md) | Per-run artifact layout, the `ARTIFACT_NAMES` contract, `RunSummary` shape. |
| [`scheduled-runs.md`](scheduled-runs.md) | How to run `run-open-prs-scheduled.ps1` on a free hosted scheduler (GitHub Actions recipe, Oracle / Fly.io alternatives, local Windows reminder). |

For the bigger picture (system rationale, design trade-offs, future plans), see
[`../design/`](../design/). For historical migration / triage notes that no
longer describe the current system, see [`../archive/`](../archive/).
