# CLI

## Purpose

Reference for the `reviewforge` command-line interface. The CLI is what the Docker container actually runs. PowerShell wrappers (`run.ps1`, `run-open-prs.ps1`) call into the same subcommands.

## Audience

- Operators running the bot in a container or on a workstation.
- Maintainers adding a new subcommand or flag.

## Entry points

| `python -m reviewforge <subcommand>` | Canonical entrypoint inside or outside the container. |

All commands call the same `cli.main()` function.


## Subcommands

### `review` — generate findings (and post by default)

```bash
python -m reviewforge review \
  --pr 1234 --org contoso --project Pay --repo api
```

What it does:

1. Build a `Config` from CLI overrides + env.
2. Validate required files (`*.prompt.md`, `standards.md`).
3. Create the artifact directory tree under `artifacts/pr-<id>/runs/<run_id>/`.
4. Run the **default 11-stage pipeline** (review + post).
5. Write `run-summary.json` and return the exit code.

Exit codes:

| Code | Meaning |
|---|---|
| `0` | Review posted, or dry-run completed. |
| `1` | A stage failed (network, parse, validation, etc.). |
| `2` | Required config was missing or invalid. |

### `post` — post a previously generated review

```bash
python -m reviewforge post --input /path/to/final-findings.json
```

What it does:

1. Validate the input JSON against `validate_review_doc`.
2. Persist it as `severity-findings.json` and `final-findings.json` so `PostToAdoStage` sees the expected shape.
3. Run the **post-only pipeline** (fetch metadata + post).
4. Write `run-summary.json`.

Use this when:

- You have a review from a previous run you want to re-post (e.g. after fixing a transient ADO outage).
- You generated the review in `--no-post` mode and now want to publish it.

The CLI flag `--no-post` (review-only) is the inverse: generates findings, prints them to stdout, does not touch ADO.

### `open-prs` — list active PRs awaiting review

```bash
python -m reviewforge open-prs
```

Returns exit 2 with a hint pointing at `./run-open-prs.ps1`. The full discovery logic lives in PowerShell (which can use `az repos pr list`); the Python package does not implement it. Use the PowerShell wrapper for batch processing.

### `discover` — list active PRs (no `az` required)

```bash
python -m reviewforge discover \
  --project Pay \
  --target-branches main,develop \
  --max 10
```

What it does:

1. Calls ADO REST's `/_apis/git/repositories/{repo}/pullRequests?searchCriteria.status=active`.
2. Paginates with `$top=100&$skip=N` until the page is short or `--max` is hit.
3. Filters by target branch if `--target-branches` is set.
4. Emits a JSON array of PR objects on stdout.

`run-open-prs.ps1` is the canonical caller of this subcommand (when migrated off `az`). Output shape:

```json
[
  {
    "pullRequestId": 123,
    "title": "Fix bug",
    "sourceRefName": "refs/heads/feature/x",
    "targetRefName": "refs/heads/main",
    "isDraft": false,
    "status": "active",
    "project": "Pay",
    "repositoryId": "...",
    "reviewers": [...],
    "createdBy": {...}
  }
]
```

Exit codes:

| Code | Meaning |
|---|---|
| `0` | JSON list printed (possibly empty). |
| `2` | Missing token, missing project, or ADO request failed. |

### `validate-config` — dry-run the configuration

```bash
python -m reviewforge validate-config
```

What it does:

1. Build a `Config` from CLI + env.
2. Call `cfg.validate_for_command("review")` and `cfg.validate_files()`.
3. Print either a friendly error report (exit 1) or a per-field summary (exit 0).

Use this in CI to fail fast on misconfiguration before kicking off a real review.

## Common flags (every subcommand)

These are added by `_build_common_parser` and inherited by `review`, `post`, `validate-config`, and (for some) `discover`. See [`configuration.md`](configuration.md) for the full env-var → flag mapping.

| Flag | Env | Notes |
|---|---|---|
| `--pr <id\|url>` | `PR_ID` / `PR_URL` | PR id or full URL. |
| `--pr-url <url>` | `PR_URL` | Full PR URL. |
| `--org <short>` | `ADO_ORG` | Required. |
| `--project <name>` | `ADO_PROJECT` | Required. |
| `--repo <id\|name>` | `ADO_REPO_ID` | Required. |
| `--source-branch <name>` | `SOURCE_BRANCH` | Override. |
| `--target-branch <name>` | `TARGET_BRANCH` | Override. |
| `--ado-token <token>` | `ADO_AUTH_TOKEN` | Bearer token. Aliases: `ADO_MCP_AUTH_TOKEN`, `ADO_API_KEY`, `SYSTEM_ACCESSTOKEN`. |
| `--pi-model <pattern>` | `PI_MODEL` | Default: `openai/gpt-5.5`. |
| `--language <lang>` | `REVIEW_LANGUAGE` | Default: `English`. |
| `--review-artifact-dir <path>` | `REVIEW_ARTIFACT_DIR` | Override the artifact root. |
| `--review-run-id <id>` | `REVIEW_RUN_ID` | Override the run id. |
| `--dry-run` | `DRY_RUN=1` | Generate findings, do not post. |
| `--no-dry-run` | `DRY_RUN=0` | Force posting (overrides `--dry-run`). |
| `--force-review` | `FORCE_REVIEW=1` | Review drafts / closed / non-policy branches. |
| `--pi-session-id <id>` | `PI_SESSION_ID` | Override the auto-derived session id. |
| `--no-pi-session` | `PI_SESSION_ENABLED=0` | Disable session reuse. |
| `--pi-session-clear` | `PI_SESSION_CLEAR=1` | Start a fresh session under the same id. |

## Programmatic entry (how-to)

You can call the orchestrator directly from another Python program:

```python
from reviewforge.config import Config
from reviewforge.pipeline.orchestrator import run_full

cfg = Config.from_env()  # or from_sources({...})
outcome = run_full(cfg)
if not outcome.success:
    raise SystemExit(outcome.exit_code)
print(outcome.summary.to_dict())  # {'pr_id': ..., 'stages': [...], ...}
```

`run_full` returns a `RunOutcome` with `exit_code`, `summary` (a `RunSummary`), and `stages` (a list of `StageResult`). The same is true for `run_review_only(cfg, output=Path(...))` and `run_post_only(cfg, input_path=Path(...))`.

`should_skip(cfg, metadata)` is a free function that returns a `dict` skip reason (or `None`) — useful for callers that want to pre-check before doing anything expensive.

## Adding a new subcommand (how-to)

1. Implement `cmd_<name>(args: argparse.Namespace) -> int` in `cli.py`. The function must return an int (the process exit code).
2. In `build_parser`, add a subparser and bind it:
   ```python
   sub.add_parser("name", parents=[common], help="…").set_defaults(
       func=cmd_name, _command="name"
   )
   ```
3. Add the subcommand to the README's CLI table.
4. Add tests in `tests/test_cli.py` (one for "parses", one for "happy path", one for "missing config").
5. If the subcommand needs new flags, add them to `_build_common_parser` (so other subcommands can inherit them) or to the subparser directly.

## Backwards compatibility

The CLI surface is stable. The Docker image invokes `python -m reviewforge`
directly, and the isolated ADO helper is available as
`python -m reviewforge.ado.cli`.
