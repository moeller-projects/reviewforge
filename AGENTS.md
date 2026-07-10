<!--
purpose: This file defines the rules of engagement for AI coding agents
         working inside this repository. Read it before proposing changes.
audience: AI coding agents (Pi, Claude Code, etc.) and human contributors
         acting on their behalf. Assumes Python 3.11+ and Docker/Podman
         familiarity.
-->

# AGENTS.md

> **Scope.** PR review bot for Azure DevOps. Model produces findings; Python owns
> all ADO side effects. Pi is invoked read-only; the `auto_pr_reviewer` package
> is the source of truth for behavior.

---

## 1. Project facts [reference]

| Item | Value |
| --- | --- |
| Language | Python ≥ 3.11 |
| Build target | OCI container (`Dockerfile`) — Docker **or** Podman (auto-detected) |
| External CLI | Pi coding agent, pinned version `0.79.1` (override via `./build.ps1 -PiVersion`) |
| LLM model pattern | e.g. `openai/gpt-5.4-mini` (set `PI_MODEL`) |
| Default test gate | `pytest --cov=auto_pr_reviewer --cov-fail-under=95` |
| LLM side effects | **None.** All Azure DevOps calls are in `auto_pr_reviewer.ado.*` |
| Idempotency contract | Every posted comment carries `prb:<key>` marker (see §6) |

---

## 2. Quickstart (host machine) [how-to]

```bash
# 1. Install dev deps
pip install -e ".[dev]"

# 2. Run the test suite locally
pytest tests/ --cov=auto_pr_reviewer --cov-fail-under=95

# 3. Validate a configuration without invoking Pi
python scripts/main.py validate-config --pr 12345
```

```powershell
# Build the container image (auto-detects docker/podman)
./build.ps1

# Review a single PR
./run.ps1 -PrUrl "https://dev.azure.com/contoso/Payments/_git/payments-api/pullrequest/1423"

# Dry run — produces findings JSON, skips ADO posting
./run.ps1 -PrUrl "https://..." -DryRun

# Batch — review every active PR
./run-open-prs.ps1

# Test inside the container
./test.ps1                       # gate = 95%
./test.ps1 -CoverageMin 0        # disable gate
```

**Env loading.** PowerShell wrappers do **not** auto-load `.env`. Load it once per
session with `set -a; source .env; set +a` (bash) or `dotenv .env` (direnv).
Precedence everywhere: **CLI flag > env var > `.env`**.

---

## 3. Repository layout [reference]

```
src/auto_pr_reviewer/        # all real logic lives here
  cli.py                     # argparse entry: review / post / validate-config / discover
  config.py                  # Config dataclass, env/.env/CLI layering, alias resolution
  ado/                       # AdoClient, posting (idempotency), diff_mapper, models
  git/                       # RepoState, prepare_repo, chunker
  ai/                        # PiRunner (subprocess wrapper) + prompt assembly
  pipeline/                  # Stage interface + orchestrator + 11 default stages
    stages/                  # fetch_pr_metadata … post_to_ado
  artifacts/                 # ARTIFACT_NAMES contract, RunSummary, file writers
scripts/                     # THIN shims — see §4
  main.py                    # ENTRYPOINT shim → auto_pr_reviewer.cli
  ado_review.py              # compat shim → auto_pr_reviewer.ado.legacy
  review.py                  # compat shim
prompts/                     # reviewer prompt fragments (markdown)
standards/clean-code.md      # default review standard
docs/                        # per-module deep dives — start with package-guide.md
tests/                       # pytest suite (gate 95%)
azure-pipelines-pr-review.yml
build.ps1 / run.ps1 / run-open-prs.ps1 / test.ps1
Dockerfile / Dockerfile.tests
```

---

## 4. Hard rules (do not break) [how-to]

These exist for real reasons. Treat as **acceptance gates**, not style suggestions.

1. **`scripts/*.py` must remain thin shims.** No business logic in
   `scripts/main.py` or `scripts/ado_review.py`. New behavior goes in
   `src/auto_pr_reviewer/`. The shims exist only to preserve the
   `ENTRYPOINT` contract and the `ado_review` module name for legacy
   PowerShell wrappers and `tests/test_ado_review.py`.

2. **The LLM must not call Azure DevOps directly.** Pi runs read-only. All
   `requests`/`httpx`/subprocess calls to ADO live in `auto_pr_reviewer.ado.*`
   and are reachable only from the `PostToAdoStage` (or its helper subprocess).

3. **Posting is idempotent.** Every comment gets a `prb:<key>` marker
   (see §6). `existing_bot_markers()` must be scanned before posting.
   Never disable dedupe to "make it work".

4. **Do not edit `ARTIFACT_NAMES` lightly.** It is the stable contract
   for `artifacts/pr-<PR_ID>/runs/<RUN_ID>/*`. Add new files at the end;
   never rename or remove an entry.

5. **Marker regex is anchored to a whole line.** `^prb:([a-zA-Z0-9]{6,32})$`
   in `src/auto_pr_reviewer/ado/posting.py`. The marker must be the **last
   line** of the comment body, on its own. Other reviewers do not have a
   `prb:` line — that's how we filter them out.

6. **Coverage gate is 95%.** New code in `src/auto_pr_reviewer/` must come
   with tests. PRs that drop below the gate fail the test stage. Override
   only with `./test.ps1 -CoverageMin 0` for throwaway experiments.

7. **`open-prs` from the Python CLI is intentionally unsupported.** It fails
   fast with a pointer to `./run-open-prs.ps1`. The architecture runs one
   container per PR — do not change this without a spec.

8. **No repo-level Node package.** The Docker image installs Pi via npm
   globally. Hosts do not need to pre-fetch PR branches when `-PrUrl` is
   used (branches are resolved via the ADO REST API).

---

## 5. The 12-stage pipeline [explanation]

`auto_pr_reviewer.pipeline.stages.DEFAULT_PIPELINE` runs in this order. Each
stage receives a mutable `StageContext`; stages may read prior outputs and
**must** write their declared artifact(s).

| # | Stage | Writes | Skipped when |
| --- | --- | --- | --- |
| 1 | `FetchPrMetadataStage` | `metadata.json`, `work-items.json`, `work-item-comments.json`, `threads.json` | dry-run + `--no-fetch` mode |
| 2 | `PrepareRepositoryStage` | `diff.patch`, `changed-files.json`, `commits.txt` | — |
| 3 | `BuildArtifactsStage` | `review-system.combined.md` | — |
| 4 | `ReconstructIntentStage` | `intent.json` (pydantic `Intent`) | — |
| 5 | `PlanContextStage` | `context-plan.json` (pydantic `ContextPlan`) | — |
| 6 | `CollectContextStage` | `collected-context.json` | — |
| 7 | `ContextDigestStage` | `context-digest.json` | — |
| 8 | `ReviewDiffStage` | `candidate-findings.json` | — |
| 9 | `VerifyFindingsStage` | `verified-findings.json` | — |
| 10 | `CalibrateSeverityStage` | `severity-findings.json`, `final-findings.json` | — |
| 11 | `AcceptanceCriteriaCoverageStage` | appends to `final-findings.json` (general-thread findings for uncovered ACs) | no linked work items, no `diff.patch`, `AC_COVERAGE_CHECK=0`, or `DRY_RUN=1` + `AC_COVERAGE_DRY_RUN=0` |
| 12 | `PostToAdoStage` | `posted-comments.json` | `DRY_RUN=1` |

A stage returns `StageStatus.OK`, `SKIPPED`, or `FAILED`. FAILED short-circuits
the run; SKIPPED writes nothing and continues. Override `should_run(ctx)` for
conditional execution; never raise from inside `should_run`.

Stage 12 also runs the **stale-comment reconciliation pass** after the create
loop: existing bot threads whose `(file, line)` is no longer in the current
diff get a `"🤖 stale — ..."` follow-up comment. Disabled via `ANNOTATE_STALE=0`.

---

## 6. Idempotent posting contract [reference]

Source of truth: `src/auto_pr_reviewer/ado/posting.py` and
[`docs/reference/ado-integration.md`](docs/reference/ado-integration.md).

**Dedupe key (`dedupe_key`) is `sha1(file|line|severity|title|message)[:12]`.**

| Field | Included? | Why |
| --- | --- | --- |
| `file` (normalized: strip leading `/`, collapse `\`) | ✅ | core location |
| `line` | ✅ | core location |
| `severity` | ✅ | impact |
| `title` | ✅ | short summary |
| `message` | ✅ | body |
| `suggestion`, `contextBasis`, `confidence`, `severity_calibration`, `created_at`, `updated_at` | ❌ | noisy / display-only; would cause false re-posts |

Marker format: `prb:<12-char-key>` on the **last line** of the comment body,
matching `^prb:([a-zA-Z0-9]{6,32})$`. The regex is intentionally restrictive
so other reviewers' comments never collide.

**Adding a new finding field to the dedupe key is a breaking change.** It will
cause every existing comment to be re-posted on the next run. Document the
change in `CHANGELOG.md` and call it out in the PR description.

---

## 7. Adding a pipeline stage [how-to]

```python
# src/auto_pr_reviewer/pipeline/stages/my_stage.py
from ..stage import Stage, StageContext, StageResult

class MyStage(Stage):
    name = "my_stage"

    def should_run(self, ctx: StageContext) -> bool:
        return True  # gate with ctx.cfg.* if conditional

    def run(self, ctx: StageContext) -> dict:
        # ctx.cfg, ctx.artifacts, ctx.metadata, ctx.intent, … are available
        # Write declared artifact(s) to ctx.artifacts.<slot>
        out = ctx.artifacts
        out.my_artifact.write_text("...", encoding="utf-8")
        return {"wrote": str(out.my_artifact)}
```

```python
# src/auto_pr_reviewer/pipeline/stages/__init__.py
DEFAULT_PIPELINE: list[Stage] = [
    ...,
    MyStage(),     # append; do not reorder existing entries
]
```

```python
# src/auto_pr_reviewer/artifacts/manager.py  — add to ARTIFACT_NAMES (end of tuple)
ARTIFACT_NAMES: tuple[str, ...] = (
    ...,
    "my-artifact.json",
)
```

```python
# src/auto_pr_reviewer/artifacts/manager.py  — add the slot to @dataclass Artifacts
@dataclass(frozen=True)
class Artifacts:
    ...
    my_artifact: Path

    def as_dict(self) -> dict[str, str]:
        return {n: str(getattr(self, ...)) for n in ARTIFACT_NAMES}
```

Add a test under `tests/` that instantiates the stage with a stub `StageContext`
and asserts the artifact exists. Run `./test.ps1` to verify the coverage gate.

---

## 8. Configuration [reference]

Loaded by `Config` in `src/auto_pr_reviewer/config.py`. Precedence:
**CLI flag > env var > `.env` > hard-coded default**. Common knobs:

| Env var | Default | Effect |
| --- | --- | --- |
| `ADO_ORG` | — | ADO org short name (required) |
| `ADO_PROJECT` | — | ADO project (required) |
| `ADO_REPO_ID` | — | repo id or name (required) |
| `PR_ID` | — | PR id (required by container entrypoint) |
| `REVIEW_LANGUAGE` | `English` | language of the reviewer output |
| `PI_MODEL` | `openai/gpt-5.5` | model pattern Pi understands |
| `DRY_RUN` | `0` | `1`/`true` → skip posting, print findings |
| `FAIL_ON` | `none` | fail check at/above `nit\|minor\|major\|blocker` |
| `VOTE_WAITING_ON` | `none` | vote "waiting author" at/above the same set |
| `MAX_DIFF_BYTES` | `200000` | per-chunk diff cap |
| `CHUNK_TRIGGER_DIFF_BYTES` | `MAX_DIFF_BYTES` | total diff threshold that switches to file-based chunking |
| `DISABLE_CHUNK_REVIEW` | `0` | `1` → force single-pass review |
| `PI_TIMEOUT_SECS` | `600` | max seconds Pi may run |
| `REVIEW_PROMPT_PATH` | baked-in | mount your own reviewer prompt |
| `REVIEW_STANDARDS_PATH` | baked-in | mount your own standards file |
| `REVIEW_ARTIFACT_DIR` | derived | per-run output path override |
| `REVIEW_RUN_ID` | timestamp-pid | deterministic run id |

To mount a custom prompt/standards in the container:

```bash
-v /path/standards.md:/cfg/standards.md -e REVIEW_STANDARDS_PATH=/cfg/standards.md
-v /path/prompt.md:/cfg/prompt.md       -e REVIEW_PROMPT_PATH=/cfg/prompt.md
```

Custom standards are appended verbatim. Custom prompts **must** preserve the
JSON output contract — see `prompts/review-system.md`.

---

## 9. Common pitfalls [how-to]

| Symptom | Cause | Fix |
| --- | --- | --- |
| Comments duplicate on every rerun | Marker regex broken or marker not on its own line | Confirm last line of comment is `prb:xxxxxxxxxxxx`; verify `^prb:([a-zA-Z0-9]{6,32})$` matches |
| `ModuleNotFoundError: auto_pr_reviewer` | `src/` not on `sys.path` | `pip install -e .` or use `scripts/main.py` (it adds `src/` automatically) |
| Build succeeds but container can't post | Build identity lacks "Contribute to pull requests" | Project Settings → Repos → Security → `<Project> Build Service` → grant Allow |
| `open-prs` errors from Python CLI | Unsupported by design | Use `./run-open-prs.ps1` |
| `FAIL_ON`/`VOTE_WAITING_ON` ignored | Wrong value | Allowed: `none`, `nit`, `minor`, `major`, `blocker` |
| Line placement off by one or two | Model maps to new-file lines; diff hunks are heuristic | Acceptable; if a finding can't be placed, set `line: null` and post as a file-level comment |
| Pi hangs past 10 min | Provider stall | `PI_TIMEOUT_SECS=600` is the ceiling — increase or split the diff |
| Coverage gate fails | New module not exercised | Add a test under `tests/test_<module>.py` covering the new public surface |

---

## 10. Out of scope (do not invent) [reference]

- Comment resolution loops on later pushes — listed as a known limitation in
  `README.md`; not implemented. Don't add it without a spec.
- In-container tool gating beyond the container boundary — out of scope per
  README; would require Pi permission config validation.
- Very-large-single-file diff truncation — partial mitigation only; see
  `CHUNK_TRIGGER_DIFF_BYTES` / `DISABLE_CHUNK_REVIEW`.
- Auto-discovery of "active PRs" from the Python CLI — `open-prs` exists but
  is intentionally a no-op pointer to PowerShell.

---

## 11. Where to look first [reference]

- New to the codebase → [`docs/reference/package-guide.md`](docs/reference/package-guide.md), then [`docs/design/architecture.md`](docs/design/architecture.md).
- Touching ADO code → [`docs/reference/ado-integration.md`](docs/reference/ado-integration.md), then
  `src/auto_pr_reviewer/ado/posting.py`.
- Touching the pipeline → [`docs/reference/pipeline.md`](docs/reference/pipeline.md), then
  `src/auto_pr_reviewer/pipeline/stage.py`.
- Touching Pi invocation → [`docs/reference/ai-runner.md`](docs/reference/ai-runner.md), then
  `src/auto_pr_reviewer/ai/runner.py`.
- Touching config / env vars → [`docs/reference/configuration.md`](docs/reference/configuration.md), then
  `src/auto_pr_reviewer/config.py`.
- Touching the prompt → [`docs/archive/ado-integration-triage.md`](docs/archive/ado-integration-triage.md) (historical), then
  `prompts/review-system.md`.

---

## 12. Open gaps / follow-ups

- Inline diagram of the stage data flow (currently only in [`docs/design/architecture.md`](docs/design/architecture.md)).
- CHANGELOG entry to require when changing `ARTIFACT_NAMES` or `dedupe_key`.
- `pre-commit` config: enforce `scripts/*.py` stays a shim (forbid new
  top-level `def`s in `scripts/main.py` and `scripts/ado_review.py`).
