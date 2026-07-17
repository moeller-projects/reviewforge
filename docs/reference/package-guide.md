# `reviewforge` package guide

## Purpose

This is the **start-here** index for the `src/reviewforge/` Python package — the application that runs inside the Docker container and reviews Azure DevOps pull requests. It orients a new reader in five minutes and points them to the right deep-dive doc for whatever they're trying to do.

The package is the **single source of truth for application logic**. PowerShell wrappers under `common.psm1`, `run.ps1`, `run-open-prs.ps1`, and the thin `src/reviewforge/*.py` shims only orchestrate the Docker invocation and forward env vars; everything else lives here.

## Audience

| Reader | What they want | Where to look |
|---|---|---|
| Operator running the bot | "How do I configure and run it?" | [`configuration.md`](configuration.md), [`cli.md`](cli.md) |
| Maintainer extending it | "How do the pieces fit? Where do I add a stage?" | [`../design/architecture.md`](../design/architecture.md), [`pipeline.md`](pipeline.md) |
| Maintainer debugging it | "Why did this stage fail? What artifacts were written?" | [`artifacts.md`](artifacts.md), [`ado-integration.md`](ado-integration.md) |
| Maintainer tuning AI cost | "How does the Pi session reuse work?" | [`ai-runner.md`](ai-runner.md) |

## What the package does, in one paragraph

`reviewforge` reviews an Azure DevOps PR by running a configurable **pipeline of stages** (fetch metadata → prepare repo → collect context → review diff in chunks → verify → calibrate severity → post to ADO). It uses the `pi` CLI as a JSON-producing subprocess for the LLM calls, talks to Azure DevOps via a small REST client, and writes a fixed set of JSON / text artifacts to `artifacts/pr-<id>/runs/<run_id>/` for every run. Configuration is loaded from CLI flags → env vars / `.env` → defaults, with an alias map so legacy env var names keep working.

## Package layout

```text
src/reviewforge/
├── __init__.py              # version constant only
├── __main__.py              # `python -m reviewforge` → cli.main()
├── cli.py                   # argparse, subcommands, _build_common_parser
├── config.py                # Config dataclass, env alias map, parse_dotenv
│
├── ado/                     # all Azure DevOps integration
│   ├── client.py            # AdoClient (REST), parse_pr_url, list_active_pull_requests
│   ├── posting.py           # dedupe_key, existing_bot_markers, should_post
│   ├── diff_mapper.py       # unified diff → ADO threadContext positions
│   ├── models.py            # PrIdentity, JsonObject
│   └── cli.py            # reviewforge.ado.cli shim (fetch-context, post-findings)
│
├── ai/                      # LLM subprocess wrapper + prompts
│   ├── runner.py            # PiRunner: pi --session-id, JSON repair
│   └── prompts.py           # system_prompt + per-stage payload builders
│
├── artifacts/               # per-run output layout
│   ├── manager.py           # Artifacts dataclass, create(), ARTIFACT_NAMES
│   ├── builder.py           # write_json, read_json, changed_files
│   └── summary.py           # RunSummary, StageRecord, finalize_run_summary
│
├── git/                     # local git orchestration
│   ├── ops.py               # RepoState, clone/fetch, GIT_ASKPASS shim
│   └── chunker.py           # file-by-file diff chunking with size cap
│
└── pipeline/                # the review pipeline
    ├── orchestrator.py      # run_full / run_review_only / run_post_only
    ├── stage.py             # Stage / StageContext / StageResult / run_stages
    ├── context.py           # ReviewContext (legacy)
    ├── schemas.py           # pydantic models for each stage's JSON output
    ├── validation.py        # validate_review_doc
    └── stages/              # 12 explicit Stage subclasses
        ├── fetch_pr_metadata.py
        ├── prepare_repository.py
        ├── build_artifacts.py
        ├── reconstruct_intent.py
        ├── plan_context.py
        ├── collect_context.py
        ├── context_digest.py
        ├── review_diff.py
        ├── verify_findings.py
        ├── calibrate_severity.py
        └── post_to_ado.py
```

## Doc index

| Doc | Mode | Summary |
|---|---|---|
| [`../design/architecture.md`](../design/architecture.md) | explanation | System components, data flow, key invariants. Start here for "how does it all fit together". |
| [`configuration.md`](configuration.md) | reference + how-to | `Config` dataclass, env var precedence, alias map, `.env` loading, validation. |
| [`cli.md`](cli.md) | reference | Subcommands (`review`, `post`, `open-prs`, `validate-config`, `discover`), flags, exit codes. |
| [`ado-integration.md`](ado-integration.md) | explanation + reference | `AdoClient` REST wrapper, idempotent posting (`dedupe_key`, `existing_bot_markers`), diff → threadContext mapping, legacy shim contract. |
|| [`pipeline.md`](pipeline.md) | explanation + reference | The `Stage` interface, the 12 default stages, ordering, how to add a new stage. |
| [`ai-runner.md`](ai-runner.md) | explanation + reference | `PiRunner` subprocess wrapper, session reuse (`--session-id`), JSON repair, prompt assembly. |
| [`artifacts.md`](artifacts.md) | reference | Artifact directory layout, `ARTIFACT_NAMES`, `RunSummary` shape, secret redaction. |

## Quick reference: the three entrypoints

| Caller | Entry | Use case |
|---|---|---|
| Operator | `./run.ps1 -PrUrl <url>` | Docker orchestration. Forwards env to a container that runs `python -m reviewforge review`. |
| Operator | `python -m reviewforge review` | Direct CLI. The container's actual command. |
| Programmatic | `from reviewforge.pipeline.orchestrator import run_full` | Library use. The orchestrator returns a `RunOutcome` with exit code and stage records. |

## Key invariants

The package has a small number of hard rules. The rest of the docs explain them in detail, but a new reader needs these in mind from the first commit:

1. **Secrets never reach the LLM subprocess.** `PiRunner._scrub_ado_env` strips `ADO_AUTH_TOKEN` / `ADO_MCP_AUTH_TOKEN` / `ADO_API_KEY` from the subprocess env before launching `pi`.
2. **Posting is idempotent.** Every posted comment carries a `prb:<12-char-key>` marker. `existing_bot_markers()` is scanned before each post so reruns do not double-post.
3. **The artifact set is a stable contract.** The 17 files in `ARTIFACT_NAMES` are written (or attempted) on every run. Downstream tooling may rely on their presence.
4. **`Config` is the single source of truth for env vars.** Application code never reads `os.getenv` directly. The only legitimate exceptions are the `parse_dotenv` helper itself and `PiRunner._scrub_ado_env` (which needs the raw env to scrub it).
5. **The pi subprocess is the only place where the model runs.** There is no direct HTTP to OpenAI / Anthropic. All prompts are file-based (`Config.review_prompt_path`, etc.) and fed via `--append-system-prompt`.

## Common questions

- **"Where does the `--session-id` value come from?"** — Default is `pr-<pr_id>-review-<run_id>`. See [`ai-runner.md`](ai-runner.md#session-reuse).
- **"Why is the orchestrator's `run_post_only` re-validating the input doc?"** — So a manually-edited `final-findings.json` cannot post malformed findings. See [`pipeline.md`](pipeline.md#run_post_only).
- **"How do I add a new review stage?"** — Implement `Stage` in `pipeline/stages/`, register it in `DEFAULT_PIPELINE`. See [`pipeline.md`](pipeline.md#extending-the-pipeline).
- **"Where does the agent get a token to clone the repo?"** — `GIT_ASKPASS_SCRIPT` in `git/ops.py` returns the bearer token when git asks for credentials. See [`ado-integration.md`](ado-integration.md#git-cloning).
