<!-- target path: pr-review-bot/README.md -->
# PR review bot (Azure DevOps + Pi)

On PR creation, a container checks out the PR branch, computes the minimal diff
against the target branch, reviews it against coding standards with the Pi coding
agent (read-only), and posts findings as PR comment threads through direct Azure
DevOps REST calls — in the language you configure.

## Design (why it's split in two)

The model does **judgment**; a script does **side effects**.

1. `scripts/main.py` is the container entrypoint. It is a thin shim that
   delegates to the `auto_pr_reviewer` Python package under `src/`. The
   package owns config loading, the explicit pipeline stages, the
   idempotent ADO posting module, and the diff line mapper. The
   `scripts/main.py` shim exists for backward compatibility with the
   Dockerfile `ENTRYPOINT` and existing PowerShell wrappers.
2. `scripts/ado_review.py` validates the review JSON, then creates one
   comment thread per finding via the Azure DevOps REST API. Every comment
   carries a hidden marker, and existing threads are scanned first, so
   **re-running the pipeline never double-posts**. The same script is
   invoked as a subprocess by the in-process pipeline (the
   `auto_pr_reviewer.ado.client.call_helper` helper) so the posting path
   stays out of the model call chain.

Pi produces pure findings JSON; a dedicated Python helper owns all Azure DevOps
interactions. This keeps the model side-effect-free and the posting path fully
deterministic and idempotent.

## Python package layout

```
src/
  auto_pr_reviewer/
    __init__.py
    __main__.py        # python -m auto_pr_reviewer
    cli.py             # argparse entrypoint: review / post / validate-config (open-prs unsupported)
    config.py          # Config dataclass, env/.env/CLI layering, alias resolution, Pi session controls
    ado/
      client.py        # AdoClient, parse_pr_url, resolve_branches, call_helper
      posting.py       # dedupe_key, existing_bot_markers, should_post, classify_threads
      diff_mapper.py   # DiffLineMapper, map_file_line_to_diff_position, AdoThreadContext
      models.py        # PrIdentity, JsonObject
    git/
      ops.py           # RepoState, prepare_repo, cleanup
      chunker.py       # DiffChunk, build_chunks
    ai/                # Pi coding-agent integration (formerly "pi")
      runner.py        # PiRunner, strip_json_fences
      prompts.py       # system_prompt, stage_instruction, review_instruction
    pipeline/
      orchestrator.py  # run_full, run_review_only, run_post_only, RunOutcome
      stage.py         # Stage, StageContext, StageResult, run_stages
      schemas.py       # pydantic models for intent / plan / digest / findings
      validation.py    # SEVERITIES, validate_stage, validate_review_doc
      context.py       # legacy ReviewContext
      stages/
        fetch_pr_metadata.py
        prepare_repository.py
        build_artifacts.py
        reconstruct_intent.py
        plan_context.py
        collect_context.py
        context_digest.py
        review_diff.py
        verify_findings.py
        calibrate_severity.py
        post_to_ado.py
    artifacts/
      manager.py       # Artifacts, create(), ARTIFACT_NAMES contract
      builder.py       # write_json, read_json, changed_files
      summary.py       # RunSummary, StageRecord, finalize_run_summary
scripts/
  main.py              # thin entrypoint (delegates to auto_pr_reviewer.cli)
  review.py            # compat shim
  ado_review.py        # thin CLI for fetch-context / post-findings
tests/                 # pytest suite
prompts/               # reviewer prompt fragments
standards/             # review standards
pyproject.toml         # minimal packaging config
```

The Docker image copies both `src/` and `scripts/` and sets
`PYTHONPATH=/app/src`. The `scripts/main.py` shim works as the container
`ENTRYPOINT` and matches the previous interface 1:1, so existing
PowerShell wrappers and the Azure pipeline definition need no changes.

## Documentation

Detailed per-module documentation lives under `docs/`:

- [`docs/package-guide.md`](docs/package-guide.md) — index, package layout, key invariants.
- [`docs/architecture.md`](docs/architecture.md) — components, data flow, design rationale.
- [`docs/configuration.md`](docs/configuration.md) — `Config` dataclass, env-var precedence, alias map, every supported env var.
- [`docs/cli.md`](docs/cli.md) — subcommands, flags, exit codes, programmatic entry.
- [`docs/ado-integration.md`](docs/ado-integration.md) — `AdoClient` REST wrapper, idempotent posting, diff → threadContext mapping, legacy shim.
- [`docs/pipeline.md`](docs/pipeline.md) — the `Stage` interface, the 11 default stages, how to add a new one.
- [`docs/ai-runner.md`](docs/ai-runner.md) — `PiRunner` subprocess wrapper, session reuse, JSON repair, prompt assembly.
- [`docs/artifacts.md`](docs/artifacts.md) — artifact directory layout, `ARTIFACT_NAMES` contract, `RunSummary` shape.

## CLI

The Python CLI is the primary entry point. All four subcommands share a
common flag set (`--org`, `--project`, `--repo`, `--pr`, `--pr-url`,
`--ado-token`, etc.) and respect the same precedence: **CLI flag >
environment variable > ``.env``**.

```bash
# Generate findings and post them (the default combined flow).
python scripts/main.py review --pr 12345

# Generate findings only (no posting). Writes final-findings.json to --output.
python scripts/main.py review --pr 12345 --no-post --output artifacts/pr-12345/review.json

# Post a previously generated review.
python scripts/main.py post --pr 12345 --input artifacts/pr-12345/review.json

# Validate the configuration for a given command and exit.
python scripts/main.py validate-config
python scripts/main.py validate-config --pr 12345

# List active PRs awaiting your review. The Python CLI does not support
# this — the architecture runs one container per pull request. Use the
# PowerShell entrypoint instead:
#   ./run-open-prs.ps1 [-Organization <url>] [-Projects <names>] ...
# Calling `open-prs` from the Python CLI fails fast with a pointer to
# the PowerShell script.

# Get help on any subcommand.
python scripts/main.py review --help
```

The container still works the same way: ``ENTRYPOINT
["/app/scripts/main.py"]``. Pass the same CLI shape as arguments to
``run.ps1`` or invoke ``python scripts/main.py review`` from inside
the container.

## Run locally (Windows / PowerShell)

Scripts are split into **build** and **run** so you can build once and iterate
on reviews without rebuilding:

| Script | Purpose |
| --- | --- |
| `build.ps1` | Build the container image |
| `run.ps1` | Run the reviewer against a PR |
| `run-open-prs.ps1` | Discover active PRs and review each one |
| `run-local.ps1` | **Deprecated.** Use `run.ps1 -Build` instead |

### Quick start — just pass the PR URL

The simplest way to run: pass the full ADO pull-request URL as the only
mandatory parameter. The script extracts org, project, repo, and PR id from
the URL and resolves source/target branches automatically via the ADO REST API.

```powershell
# Optional: copy defaults into a local config file
Copy-Item .env.example .env
# Edit .env, then build/run can read from it.

# Build once
./build.ps1

# Review a PR — just the URL, branches auto-detected
./run.ps1 -PrUrl "https://dev.azure.com/contoso/Payments/_git/payments-api/pullrequest/1423"

# Dry run: iterate on prompt/standards without posting to the PR
./run.ps1 -PrUrl "https://dev.azure.com/contoso/Payments/_git/payments-api/pullrequest/1423" -DryRun

# Review all active PRs across the org (config already in shell env,
# e.g. loaded by direnv from .env):
./run-open-prs.ps1
```

### Configuration via `.env` (recommended)

The `.env` file at the repo root is a **reference / template** of
all operational settings — language, fail threshold, model name,
image tag, ADO identity, etc. The PowerShell wrappers do NOT
auto-load it. Pick whichever loader matches your shell and load it
once per session:

```bash
# bash / zsh
set -a; source .env; set +a

# direnv (recommended for per-directory auto-load)
echo 'dotenv .env' > .envrc && direnv allow
```

Once loaded, the variables are part of the process env and are
read by the wrappers the same way as any other env var. Script
parameters (`-AdoToken`, `-DryRun`, ...) still win for the
duration of a single invocation. Copy `.env.example` to `.env`
and fill in at minimum:

```dotenv
# Required for both run.ps1 and run-open-prs.ps1
ADO_AUTH_TOKEN=<your-pat>
OPENAI_API_KEY=<your-key>

# Required for run-open-prs.ps1
ADO_ORGANIZATION=https://dev.azure.com/<your-org>/
ADO_PROJECTS=<proj1>,<proj2>
ADO_TARGET_BRANCHES=main,master,develop,dev

# Required for run.ps1 (when no -PrUrl is given)
ADO_ORG=<your-org>
ADO_PROJECT=<project>
ADO_REPO_ID=<repo-name-or-guid>
PR_ID=<int>

# Optional
REVIEW_LANGUAGE=English
PI_MODEL=openai/gpt-5.4-mini
DRY_RUN=0
```

### `run-open-prs.ps1` interactive mode

When the terminal is a TTY (or `-Interactive` is passed), the
script lists the discovered PRs and asks which to review:

```powershell
==> Found 3 active pull request(s):
  [ 1] PR #8388  Laekker.Kitchen/Laekker.Kitchen -> main  Fix allergen mapping…
  [ 2] PR #8390  Laekker.Kitchen/Laekker.Kitchen -> main  Fix allergen backfill…
  [ 3] PR #8392  Laekker.Kitchen/Laekkerai.Ordering -> dev  Normalize phone numbers…
  [all] review all  |  [none] cancel
Selection: 1,3
```

Selection syntax: ``1,3-5``, ``all``, ``none``, ``a``, ``n``.
Invalid input re-prompts. ``none`` exits cleanly with no runs.

### Build + run in one call

Replace the deleted `run-local.ps1` with `-Build` on either entrypoint:

```powershell
./run.ps1 -PrUrl "https://dev.azure.com/.../pullrequest/1423" -Build
./run-open-prs.ps1 -Build
```

### Script parameter matrix (current)

| Script | Required params | Common optional params |
| --- | --- | --- |
| `build.ps1` | (none) | `-Image`, `-PiVersion`, `-EnvFile` |
| `run.ps1` | `-PrUrl` *or* (`-Org` + `-Project` + `-RepoId` + `-PrId`) | `-AdoToken`, `-DryRun`, `-Build`, `-EnvFile` |
| `run-open-prs.ps1` | `-Organization`, `-Projects`, `-TargetBranches` (or `.env` values) | `-AdoToken`, `-DryRun`, `-Interactive`, `-MaxPullRequests`, `-Build`, `-EnvFile` |

Most settings now live in `.env`. The explicit params above are
shortcuts for the per-invocation override path.

Prereqs: Docker Desktop and a model key in `$env:OPENAI_API_KEY`, plus either
`az login` (for the token) or an explicit `-AdoToken`. `-DryRun` still needs the
token for the in-container clone, but it skips posting and just prints the
findings JSON.

### PowerShell is now a thin wrapper

The PowerShell scripts are intentionally minimal. They only:

* parse CLI args and forward them as environment variables,
* read the live process env (which the user is responsible for populating — the `.env` at the repo root is a reference / template, not auto-loaded),
* pick a container runtime (`docker` or `podman`),
* run the container with the env vars and delete the temp env file in a `finally` block.

All Azure DevOps logic — REST calls, PR URL parsing, branch normalization,
reviewer lookup, branch resolution, JSON validation, severity calibration,
vote / post — lives in the Python package `auto_pr_reviewer` and runs inside
the container. The local `common.psm1` only contains:

* `Write-Step`, `Fail` (output helpers)
* `Get-ContainerRuntime` (docker/podman detection)
* `Get-EnvOrDefault` (env-var lookup with default)
* `Resolve-ScriptConfig` (read process env, layer CLI overrides, validate required keys)
* `ConvertFrom-CommaList` (csv / array normalization)
* `Show-InteractivePrompt` (REPL menu for selecting PRs)
* `Write-EnvFile` (string-concat helper that builds the temp env file)

If you need new ADO behavior, add it to the Python package and call it from
the container. Do not extend `common.psm1` or the individual `*.ps1`
wrappers with ADO logic.

### Run tests

The test suite enforces a 95% minimum coverage threshold on the
`auto_pr_reviewer` package.

```bash
# Locally (no coverage gate):
pytest tests/

# Locally with the coverage gate:
pytest tests/ --cov=auto_pr_reviewer --cov-fail-under=95

# In Docker via the PowerShell helper (default gate = 95%):
./test.ps1
./test.ps1 -NoBuild                # reuse existing image
./test.ps1 -CoverageMin 80         # override the gate locally
./test.ps1 -CoverageMin 0          # disable the gate entirely
```

The project no longer uses a repo-level Node package; the Docker image still
installs the Pi CLI via npm globally.

> The reviewer's fetch happens inside the container, so the host does not need to
> pre-fetch the PR branches. When using `-PrUrl`, the source and target branches
> are resolved from the ADO REST API — no need to specify them manually.

## Setup

1. **Build the image** (auto-detects podman or docker):
   ```powershell
   ./build.ps1
   ```
2. **Pipeline definition** — create the pipeline from
   `azure-pipelines-pr-review.yml`.
3. **Pipeline variables** — set `ADO_ORG` (org short name) and `REVIEW_LANGUAGE`,
   and add a **secret** variable `OPENAI_API_KEY` (Pi's model provider key).
   Optional variables in the YAML include `FAIL_ON`, `VOTE_WAITING_ON`,
   `DRY_RUN`, and `PI_VERSION`.
4. **Grant the build identity permission to comment.** Project Settings →
   Repositories → your repo → Security → select **`<Project> Build Service (<org>)`**
   → set **Contribute to pull requests = Allow**. Without this, `System.AccessToken`
   can read but every comment call fails.
5. **Wire the PR trigger as a branch policy** (Azure Repos Git does not use YAML
   `pr:`): Project Settings → Repositories → your repo → Policies → select the
   target branch (e.g. `main`) → **Build Validation → +** → pick this pipeline →
   trigger Automatic, optionally not blocking. The policy runs the pipeline on every
   PR into that branch and re-runs on each push.

## Configuration (env the container reads)

| Variable | Required | Default | Purpose |
| --- | --- | --- | --- |
| `ADO_ORG` | yes | — | Org short name, e.g. `contoso` |
| `ADO_PROJECT` | yes | — | Project (pipeline: `$(System.TeamProject)`) |
| `ADO_REPO_ID` | yes | — | Repo id or name (`$(Build.Repository.Name)`) |
| `PR_ID` | yes | — | PR id (`$(System.PullRequest.PullRequestId)`) |
| `SOURCE_BRANCH` / `TARGET_BRANCH` | yes | — | PR branches (refs or short names) |
| `ADO_AUTH_TOKEN` | yes | — | Bearer token; pass `$(System.AccessToken)` |
| `OPENAI_API_KEY` | yes | — | Pi's model provider key |
| `REVIEW_LANGUAGE` | no | `English` | Language for all comment text |
| `PI_MODEL` | no | `openai/gpt-5.5` | Model pattern Pi understands (e.g. `openai/gpt-5.4-mini`) |
| `REVIEW_PROMPT_PATH` | no | baked-in | Custom reviewer prompt (mount to override) |
| `REVIEW_STANDARDS_PATH` | no | baked-in | Custom standards (mount to override) |
| `MAX_DIFF_BYTES` | no | `200000` | Per-chunk diff cap after chunking starts; only oversized single-file diffs are truncated |
| `CHUNK_TRIGGER_DIFF_BYTES` | no | `MAX_DIFF_BYTES` | Total diff-size threshold for switching from one rich-context review to file-based chunking |
| `DISABLE_CHUNK_REVIEW` | no | `0` | Set to `1`/`true` to keep large diffs in one pass instead of chunking |
| `PI_TIMEOUT_SECS` | no | `600` | Max seconds the Pi reviewer may run (prevents hangs) |
| `FAIL_ON` | no | `none` | Fail the check at/above `nit\|minor\|major\|blocker` |
| `VOTE_WAITING_ON` | no | `major` | Vote “waiting for author” at/above `nit\|minor\|major\|blocker`, or `none` |

Artifacts are persisted in the named Docker/Podman volume `pr-review-bot-artifacts` mounted at `/workspace/artifacts`. Each invocation writes to a run-scoped directory: `pr-<id>/runs/<run-id>/`, and `pr-<id>/latest.txt` points to the most recent run.

## Version pinning

The default build input is pinned for reproducibility:

- Pi coding agent: `0.79.1`

Override the Pi version explicitly with `./build.ps1 -PiVersion ...` when you want to upgrade it deliberately.

## Custom prompt / standards

Mount your own and point the env at them:
```
-v /path/standards.md:/cfg/standards.md -e REVIEW_STANDARDS_PATH=/cfg/standards.md
-v /path/prompt.md:/cfg/prompt.md       -e REVIEW_PROMPT_PATH=/cfg/prompt.md
```
The standards file is appended to the reviewer prompt verbatim. The prompt must
keep the JSON output contract intact (see `prompts/review-system.md`).

## Artifacts

Every run writes its output to ``artifacts/pr-<PR_ID>/runs/<RUN_ID>/``.
The set of files is a stable contract; see
:data:`auto_pr_reviewer.artifacts.ARTIFACT_NAMES`.

| File                          | Meaning                                                    |
|-------------------------------|------------------------------------------------------------|
| ``metadata.json``             | Resolved PR title/status/branches                          |
| ``diff.patch``                | Unified diff for the merge-base range                      |
| ``changed-files.json``        | Per-file language + ``isTest`` classification               |
| ``commits.txt``               | ``git log --oneline`` for the PR range                     |
| ``intent.json``               | Reconstructed PR intent (pydantic-validated)               |
| ``context-plan.json``         | Pi's plan for what to read / search                        |
| ``collected-context.json``    | Files/tests/search hits collected deterministically        |
| ``context-digest.json``       | Pi's digest of the collected context                       |
| ``candidate-findings.json``   | First-pass findings before verification                    |
| ``verified-findings.json``    | Findings after adversarial verification                    |
| ``severity-findings.json``    | Findings after severity calibration                        |
| ``final-findings.json``       | The doc posted (or printed) to ADO                         |
| ``posted-comments.json``      | Counts of created/skipped/duplicate comments and vote info |
| ``run-summary.json``          | High-level diagnostics (per-stage timing, exit code, etc.) |
| ``review-system.combined.md`` | Concatenated system prompt fed to Pi                      |

Use ``--review-artifact-dir`` (or the env var ``REVIEW_ARTIFACT_DIR``) to
override the per-run directory, or ``--review-run-id`` for deterministic
output paths. ``pr-<PR_ID>/latest.txt`` always points to the most recent
run for that PR.

## Pipeline stages

The pipeline is composed of explicit :class:`Stage` instances in
:data:`auto_pr_reviewer.pipeline.stages.DEFAULT_PIPELINE`:

1. ``FetchPrMetadataStage`` — call the ADO helper to populate
   ``metadata.json``, ``work-items.json``, ``work-item-comments.json``,
   ``threads.json``.
2. ``PrepareRepositoryStage`` — shallow-clone the PR branches and write
   ``diff.patch``, ``changed-files.json``, ``commits.txt``.
3. ``BuildArtifactsStage`` — write the combined system prompt.
4. ``ReconstructIntentStage`` — ask Pi for ``intent.json`` (validated as
   :class:`auto_pr_reviewer.pipeline.schemas.Intent`).
5. ``PlanContextStage`` — ask Pi for ``context-plan.json`` (validated as
   :class:`ContextPlan`).
6. ``CollectContextStage`` — read files / tests / searches from the
   ``context-plan.json``; write ``collected-context.json``.
7. ``ContextDigestStage`` — ask Pi to digest the collected context.
8. ``ReviewDiffStage`` — produce ``candidate-findings.json``. Splits the
   diff into file-based chunks when it exceeds
   ``CHUNK_TRIGGER_DIFF_BYTES``.
9. ``VerifyFindingsStage`` — ask Pi to adversarially verify the
   candidate findings; emit ``verified-findings.json``.
10. ``CalibrateSeverityStage`` — ask Pi to recalibrate severities;
    emit ``severity-findings.json`` and ``final-findings.json``.
11. ``PostToAdoStage`` — post the final findings. No-op when
    ``DRY_RUN=1``; otherwise calls the legacy helper. Writes
    ``posted-comments.json``.

The :class:`Stage` interface (``run(ctx) -> StageResult``) makes every
stage individually testable; the runner stops on the first failure and
records timings, status, and high-level counts in ``run-summary.json``.

## Idempotent posting

Posting decisions live in :mod:`auto_pr_reviewer.ado.posting`:

* :func:`dedupe_key` returns a stable 12-char hex key for a finding,
  derived from its file, line, severity, title, and message. Confidence
  and suggestion are intentionally excluded so reruns with minor model
  variation do not change the key.
* :func:`existing_bot_markers` scans the PR's threads and returns the
  set of bot markers already present.
* :func:`should_post` returns ``True`` iff a finding's key is not in
  the existing set.
* :func:`classify_threads` partitions threads into bot-authored and
  human-authored; the reviewer never touches human comments.
* :func:`attach_marker` returns the key and the full ``prb:<key>``
  marker that the poster appends to the comment body.

``posted-comments.json`` records the outcome (``created``, ``skipped``,
``skipped_reasons``, ``votedWaitingForAuthor``) for every run.

## Diff line mapping

Mapping a finding to an inline ADO thread is handled by
:mod:`auto_pr_reviewer.ado.diff_mapper`. The public entrypoint is
:func:`map_file_line_to_diff_position`:

```python
ctx = map_file_line_to_diff_position("src/app.py", 42, diff_text=diff)
if ctx is None:
    ctx = map_file_to_fallback("src/app.py", diff_text=diff)  # file-level
# ctx.to_thread_context() -> ADO-shaped dict
```

The mapper supports added lines, context (modified) lines, renamed
files, multiple hunks per file, and files with no trailing newline. If
the line is not in any hunk, it returns ``None`` so the caller can
fall back to a file-level or summary comment. A pre-built
:class:`DiffLineMapper` can be reused across many calls for the same
diff.

## Known limitations (name them, don't paper over them)

- **Line placement is best-effort.** The model maps findings to new-file line
  numbers from the diff; it can be off by a line or two. When unsure it sets
  `line: null` and the comment lands as a general PR thread naming the file.
- **No comment resolution loop.** Findings fixed in a later push aren't auto-resolved;
  the summary is posted once and not updated. (Add `repo_update_pull_request` /
  resolve calls if you want this.)
- **Token-scoped, not tool-gated inside the container.** Enforcement is the
  container boundary + read-only Pi tools + a token that can only comment. If you
  want in-container tool gating too, drop in your Pi permission config — verify the
  fork's schema first.
- **Very large single-file diffs still truncate.** Once a PR exceeds
  `CHUNK_TRIGGER_DIFF_BYTES`, it is split into file-based chunks up to
  `MAX_DIFF_BYTES`, but one oversized file diff is still truncated and called
  out in the summary. Set `DISABLE_CHUNK_REVIEW=1` to force a single-pass
  review instead.
