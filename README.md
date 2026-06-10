<!-- target path: pr-review-bot/README.md -->
# PR review bot (Azure DevOps + Pi + azure-devops-mcp)

On PR creation, a container checks out the PR branch, computes the minimal diff
against the target branch, reviews it against coding standards with the Pi coding
agent (read-only), and posts findings as PR comment threads through the official
`@azure-devops/mcp` server — in the language you configure.

## Design (why it's split in two)

The model does **judgment**; a script does **side effects**.

1. `scripts/review.sh` builds the merge-base diff (`git diff target...source`) and
   runs Pi with only read tools (`read,grep,find,ls`). Pi cannot write to the repo
   or the PR. Its final output is a strict JSON findings contract.
2. `scripts/post-findings.mjs` validates that JSON, then creates one comment thread
   per finding via the MCP tool `repo_create_pull_request_thread`. Every comment
   carries a hidden marker, and existing threads are scanned first, so **re-running
   the pipeline never double-posts**.

Pi has no built-in MCP, so letting the model post directly would mean an extra
extension plus non-deterministic, non-idempotent side effects. The poster owns
posting instead. The MCP server is still what creates the comments.

## Run locally (Windows / PowerShell)

Scripts are split into **build** and **run** so you can build once and iterate
on reviews without rebuilding:

| Script | Purpose |
| --- | --- |
| `scripts/build.ps1` | Build the container image |
| `scripts/run.ps1` | Run the reviewer against a PR |
| `scripts/run-open-prs.ps1` | Review every active PR assigned to you except ones where your vote is “waiting for author” |
| `run-local.ps1` | Convenience: build + run in one call |

### Quick start — just pass the PR URL

The simplest way to run: pass the full ADO pull-request URL as the only
mandatory parameter. The script extracts org, project, repo, and PR id from
the URL and resolves source/target branches automatically via the ADO REST API.

```powershell
# Build once
./scripts/build.ps1

# Review a PR — just the URL, branches auto-detected
./scripts/run.ps1 -PrUrl "https://dev.azure.com/contoso/Payments/_git/payments-api/pullrequest/1423"

# Dry run: iterate on prompt/standards without posting to the PR
./scripts/run.ps1 -PrUrl "https://dev.azure.com/contoso/Payments/_git/payments-api/pullrequest/1423" -DryRun

# German comments, cheaper model
./scripts/run.ps1 -PrUrl "https://dev.azure.com/contoso/Payments/_git/payments-api/pullrequest/1423" -Language German -PiModel openai/gpt-5.4-mini

# Review all active PRs where you are a reviewer and not waiting for author
./scripts/run-open-prs.ps1 -Org contoso -Project Payments -RepoId payments-api
```

### Legacy invocation (individual params)

If you prefer, you can still pass each parameter separately. Branches are
auto-resolved from the ADO REST API unless you override them:

```powershell
./scripts/run.ps1 -Org contoso -Project Payments -RepoId payments-api -PrId 1423
```

### Batch mode: review all open PRs assigned to you

Use `scripts/run-open-prs.ps1` when you want Windows/PowerShell to scan a repo
for active PRs where **you** are a reviewer, skip PRs where your reviewer vote
is already **waiting for author** (`-5`), and then launch one containerized
review run per remaining PR.

```powershell
# Review every matching PR
./scripts/run-open-prs.ps1 -Org contoso -Project Payments -RepoId payments-api

# Cap the batch size and avoid posting while iterating
./scripts/run-open-prs.ps1 -Org contoso -Project Payments -RepoId payments-api -MaxPullRequests 5 -DryRun
```

The script reuses the same auth flow as `scripts/run.ps1`: pass `-AdoToken` or
sign in with `az login`, and provide `$env:OPENAI_API_KEY` (or `-OpenAiApiKey`).

### All-in-one: `run-local.ps1`

The original script still works and now also supports `-PrUrl`:

```powershell
# New style (recommended)
./run-local.ps1 -PrUrl "https://dev.azure.com/contoso/Payments/_git/payments-api/pullrequest/1423" -DryRun

# Legacy style (still works)
./run-local.ps1 -Org contoso -Project Payments -RepoId payments-api `
    -PrId 1423 -SourceBranch feature/x -SkipBuild
```

Prereqs: Docker Desktop and a model key in `$env:OPENAI_API_KEY`, plus either
`az login` (for the token) or an explicit `-AdoToken`. `-DryRun` still needs the
token for the in-container clone, but it skips posting and just prints the
findings JSON.

### Run tests

```bash
npm test
```

This runs the Node unit tests for `scripts/post-findings.mjs` plus smoke tests
that exercise `scripts/review.sh` with stubbed git/Pi tooling.

> The reviewer's fetch happens inside the container, so the host does not need to
> pre-fetch the PR branches. When using `-PrUrl`, the source and target branches
> are resolved from the ADO REST API — no need to specify them manually.

## Setup

1. **Build the image** (auto-detects podman or docker):
   ```powershell
   ./scripts/build.ps1
   ```
2. **Pipeline definition** — create the pipeline from
   `azure-pipelines-pr-review.yml`.
3. **Pipeline variables** — set `ADO_ORG` (org short name) and `REVIEW_LANGUAGE`,
   and add a **secret** variable `OPENAI_API_KEY` (Pi's model provider key).
   Optional variables in the YAML include `FAIL_ON`, `VOTE_WAITING_ON`,
   `DRY_RUN`, `PI_VERSION`, and `ADO_MCP_VERSION`.
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
| `ADO_MCP_AUTH_TOKEN` | yes | — | Bearer token; pass `$(System.AccessToken)` |
| `OPENAI_API_KEY` | yes | — | Pi's model provider key |
| `REVIEW_LANGUAGE` | no | `English` | Language for all comment text |
| `PI_MODEL` | no | `openai/gpt-5.5` | Model pattern Pi understands (e.g. `openai/gpt-5.4-mini`) |
| `REVIEW_PROMPT_PATH` | no | baked-in | Custom reviewer prompt (mount to override) |
| `REVIEW_STANDARDS_PATH` | no | baked-in | Custom standards (mount to override) |
| `MAX_DIFF_BYTES` | no | `200000` | Per-chunk diff cap after chunking starts; only oversized single-file diffs are truncated |
| `CHUNK_TRIGGER_DIFF_BYTES` | no | `MAX_DIFF_BYTES` | Total diff-size threshold for switching from one rich-context review to file-based chunking |
| `PI_TIMEOUT_SECS` | no | `600` | Max seconds the Pi reviewer may run (prevents hangs) |
| `FAIL_ON` | no | `none` | Fail the check at/above `nit\|minor\|major\|blocker` |
| `VOTE_WAITING_ON` | no | `major` | Vote “waiting for author” at/above `nit\|minor\|major\|blocker`, or `none` |

## Version pinning

The default build inputs are pinned for reproducibility:

- Pi coding agent: `0.79.1`
- Azure DevOps MCP: `2.7.0`
- MCP SDK dependency: `1.29.0`

Override the image tool versions explicitly with `./scripts/build.ps1 -PiVersion ... -AdoMcpVersion ...` when you want to upgrade them deliberately.

## Custom prompt / standards

Mount your own and point the env at them:
```
-v /path/standards.md:/cfg/standards.md -e REVIEW_STANDARDS_PATH=/cfg/standards.md
-v /path/prompt.md:/cfg/prompt.md       -e REVIEW_PROMPT_PATH=/cfg/prompt.md
```
The standards file is appended to the reviewer prompt verbatim. The prompt must
keep the JSON output contract intact (see `prompts/review-system.md`).

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
  out in the summary.
