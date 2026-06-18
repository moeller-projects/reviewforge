# ADO integration

## Purpose

Document how `auto_pr_reviewer` talks to Azure DevOps: the REST client, idempotent posting, the diff-line mapper, and the legacy subprocess shim. This is **explanation + reference** for the `auto_pr_reviewer.ado` subpackage.

## Audience

- Maintainers changing how the bot reads or writes to ADO.
- Operators debugging a posting failure.

## Module map

```text
auto_pr_reviewer.ado/
├── client.py     # AdoClient (REST), URL parsing, list_active_pull_requests, call_helper
├── posting.py    # dedupe_key, existing_bot_markers, should_post, make_marker
├── diff_mapper.py # DiffLineMapper, AdoThreadContext, parse_unified_diff
├── models.py     # PrIdentity, JsonObject
└── legacy.py     # scripts/ado_review.py shim (fetch-context, post-findings)
```

`client.py` and `posting.py` are independent and pure (no shared state). `diff_mapper.py` is used by `legacy.py` and by `post_to_ado` to anchor inline comments. `legacy.py` is the subprocess-friendly CLI surface preserved for back-compat.

## `AdoClient` (the REST wrapper)

A small class with five verbs and three generic helpers:

```python
class AdoClient:
    def __init__(self, org, project, repo, token=None): ...
    def get_pr(pr_id, *, include_work_item_refs=False) -> dict
    def get_threads(pr_id) -> list[dict]
    def create_thread(pr_id, body) -> Any
    def vote(pr_id, reviewer_id, vote) -> Any
    def connection_data() -> dict
    def get(path) / post(path, body) / put(path, body)  # generic
```

Implementation notes:

- Uses `urllib.request` (no SDK). The only dependencies are `json` and `urllib`.
- All requests carry `Authorization: Bearer <token>` and `Accept: application/json; api-version=7.0`.
- 60-second timeout, no retries, no rate-limit handling. Failures are loud: HTTP errors log the URL, method, status, and the response body to stderr, then re-raise.
- The token is resolved at construction time via `resolve_token()` if not passed explicitly.

### URL parsing

`parse_pr_url(value)` accepts both `dev.azure.com` and `<org>.visualstudio.com` URLs and returns `(org, project, repo, pr_id)`. `parse_pr_identity` wraps it as a `PrIdentity` dataclass. Both raise `SystemExit` on a malformed URL — the CLI converts that into a clean error message.

### Branch resolution

`resolve_branches(cfg)` returns `(source_short, target_short)`:

- If `cfg.source_branch` and `cfg.target_branch` are both set, return them as-is.
- Otherwise, fetch the PR via `get_pr` and extract `sourceRefName` / `targetRefName`, stripping the `refs/heads/` prefix.

This means the rest of the pipeline never needs to handle ref-name prefixes.

### Discovery

`list_active_pull_requests(cfg, *, project=None, target_branches=None, max_results=0)` is the Python replacement for the `az repos pr list` shell-out in `run-open-prs.ps1`. It:

- Paginates `/_apis/git/repositories/{repo}/pullRequests?searchCriteria.status=active` with `$top=100&$skip=N`.
- Optionally filters by target branch (matching short names like `main` or full refs like `refs/heads/main`).
- Optionally caps the result count.
- Returns the same shape `get_pr` returns, plus a `project` field.

The `discover` CLI subcommand is a thin wrapper that prints this list as JSON to stdout.

## Idempotent posting

The reviewer must not double-post when re-run on the same PR. The contract:

1. Every posted comment carries a stable marker of the form `prb:<12-char-key>` on a line by itself.
2. Before posting, the reviewer scans existing PR threads for these markers and skips findings whose marker is already present.
3. `dedupe_key(finding)` is the canonical way to compute the marker from a finding.

### `dedupe_key`

```python
def dedupe_key(finding: dict[str, Any]) -> str:
    raw = "|".join([
        _normalize_file(finding.get("file")),
        str(finding.get("line") or ""),
        str(finding.get("severity") or ""),
        str(finding.get("title") or ""),
        str(finding.get("message") or ""),
    ])
    return hashlib.sha1(raw.encode("utf-8")).hexdigest()[:12]
```

The 12-char prefix is short enough to read in a thread, unique enough to be stable across reruns, and intentionally **excludes** `confidence`, `suggestion`, `evidence`, and other noisy fields. The key is stable across minor model variation in those fields.

`_normalize_file` strips a leading `/` and collapses `\` to `/` so the same file hashes identically whether it appears as `src/app.ts` or `/src/app.ts`.

### `existing_bot_markers`

```python
def existing_bot_markers(threads: Iterable[dict[str, Any]]) -> set[str]:
    """Return the set of bot markers already present in the given PR threads."""
```

Walks every thread, scans each comment body for `^prb:([a-zA-Z0-9]{6,32})$`, and returns the set of keys found. The caller (the posting stage) subtracts this set from the new findings' keys before posting.

The regex is anchored to a line of its own. Other reviewers' comments will not have a `prb:` marker on a line by itself, so they do not pollute the set.

### `should_post`

A higher-level helper that combines the dedupe check with the file-mapping check. Returns `(should_post, reason)` so callers can record why a finding was skipped.

## Diff → threadContext mapping

`AdoClient.create_thread` requires a `threadContext` to anchor an inline comment to a specific line. ADO's expected shape is:

```json
{
  "filePath": "/src/app.ts",
  "rightFileStart": {"line": 42, "offset": 1},
  "rightFileEnd":   {"line": 42, "offset": 1},
  "position": 7
}
```

`auto_pr_reviewer.ado.diff_mapper` builds an in-memory index of the unified diff that maps each changed line in the new file to its hunk's start position.

### `DiffLineMapper`

```python
@classmethod
def from_text(cls, diff_text: str) -> "DiffLineMapper"
def find(self, file_path: str, new_line: int | None) -> AdoThreadContext | None
def file_level_context(self, file_path: str) -> AdoThreadContext | None
```

- `from_text` parses the diff once and builds an index. The index is reused across many `find` calls.
- `find(file, line)` returns the context for the line if the file/line is in the diff, else `None`. Returning `None` lets the caller fall back to a file-level comment or skip the finding entirely (to avoid HTTP 400).
- `file_level_context(file)` returns a context for the first changed line of the file, which is what ADO accepts when the exact line is unknown but the file is in the diff.

The mapper is robust to:

- Multiple files in the diff.
- Hunks of varying length.
- Mixed added/kept/deleted lines.
- Files whose diff is empty (returns `None`).
- Files whose diff is malformed (silently returns `None` rather than raising).

## Git cloning

The `git/ops.py` module handles the local clone + checkout of the PR's source. The interesting part is the credential plumbing:

```python
GIT_ASKPASS_SCRIPT = """\
#!/usr/bin/env python3
import os, sys
print('x-access-token' if sys.argv[1].lower().find('username') >= 0 else os.environ['ADO_AUTH_TOKEN'])
"""
```

When git needs credentials, it invokes a small Python helper via `GIT_ASKPASS`. The helper returns `x-access-token` for the username prompt and the ADO token for the password prompt. This is the canonical Azure DevOps PAT-over-HTTPS dance; the token never appears in `ps` or command-line history.

The token is read from the **subprocess env** at the moment git calls the helper. Because the orchestrator's `RunSummary` strips the token from the summary, and because the Git subprocess inherits the orchestrator's env (which has the token set), this works transparently.

## Legacy shim (`scripts/ado_review.py` → `auto_pr_reviewer.ado.legacy`)

The Docker image and CI scripts still shell out to `scripts/ado_review.py` for the `fetch-context` and `post-findings` subcommands. The script is a 30-line shim:

```python
# scripts/ado_review.py
import sys
from auto_pr_reviewer.ado.legacy import main as legacy_main
sys.exit(legacy_main(sys.argv[1:]))
```

The shim adds the `src/` directory to `sys.path` so the package import works on hosts where the package is not installed.

### The `legacy.py` module

All the actual logic lives in `auto_pr_reviewer.ado.legacy`. It exposes two CLI subcommands:

| Subcommand | Purpose |
|---|---|
| `fetch-context` | Fetch the PR's metadata, work items, and existing threads; write to `<artifact_dir>/{metadata,work-items,threads}.json` and `diff.patch`. |
| `post-findings` | Validate the input findings JSON, apply the env-driven filters (POST_MIN_SEVERITY, DROP_LOW_CONFIDENCE, REQUIRE_CONTEXT_FOR, MAX_FINDINGS), diff-map each finding's file/line, and post via the `AdoClient` with idempotent marker-based dedup. |

The module also re-exports a few compatibility shims that older test suites and external consumers expect:

- `enc(value)` — URL-encode a value (was a one-liner in the original script).
- `token()` / `org()` / `project()` / `repo()` — env-var readers that raise `SystemExit` on missing values, matching the original `scripts/ado_review.py` API.
- `validate_findings(doc)`, `worst_rank(findings)`, `should_threshold(findings, threshold)` — small helpers that the original script had.
- `dedupe_key` is also re-exported under the alias `key_of`.

### Why is the shim still around?

Two reasons:

1. The Docker image is built around the script. Removing the script would require updating the image's `CMD`/`ENTRYPOINT` and re-validating the CI matrix.
2. `PostToAdoStage` uses `call_helper` to invoke the script as a subprocess, which keeps the in-process orchestrator out of the posting code path. This is a deliberate boundary: a malformed finding cannot crash the orchestrator; it crashes the subprocess, which the orchestrator reports as a stage failure.

### When to remove it

When the Docker image is next refactored, the right migration is:

1. Inline the `post-findings` logic into `PostToAdoStage.run` (calling `AdoClient` directly instead of via subprocess).
2. Inline the `fetch-context` logic into `FetchPrMetadataStage`.
3. Drop `call_helper`, `scripts/ado_review.py`, and the `auto_pr_reviewer.ado.legacy` module.

For now, the shim is a stable back-compat surface. Tests assert its presence (`tests/test_entry_points.py::test_shim_main_delegates`).

## Posting format (reference)

The shape of a posted thread:

```json
{
  "comments": [
    {
      "content": "🟠 major: Token in log\n\n…body…\n\nprb:ea6802f947d4\n",
      "commentType": 1
    }
  ],
  "status": 1,
  "threadContext": {
    "filePath": "/src/log.ts",
    "rightFileStart": {"line": 10, "offset": 1},
    "rightFileEnd":   {"line": 10, "offset": 1}
  }
}
```

The marker is always the last line of the comment body, on its own line, so the regex `^prb:([a-zA-Z0-9]{6,32})$` finds it cleanly.

## Customizing the comment format

The default layout (the markdown rendered by the original `commentBody()`)
ships in `auto_pr_reviewer.ado.comment_format.DefaultCommentFormatter` and
is selected when no override is configured. To use a different layout,
point `COMMENT_TEMPLATE_PATH` at a Jinja2 template file:

```dotenv
# .env
COMMENT_TEMPLATE_PATH=./pr-comment.md
```

The template is plain Markdown. The body of each finding is rendered with
this context:

| Placeholder | Source |
|-------------|--------|
| `{{ title }}` | finding title |
| `{{ message }}` | finding message (body) |
| `{{ severity }}` | `major` / `minor` / `nit` / `blocker` |
| `{{ severity_label }}` | `🟠 major` etc. (emoji + label) |
| `{{ confidence }}` | `high` / `medium` / `low` (or `""`) |
| `{{ context_basis }}` | `contextBasis` field (or `""`) |
| `{{ suggestion }}` | suggested change (or `""`) |
| `{{ file }}` | source file |
| `{{ line }}` | line number |
| `{{ key }}` | raw dedupe key |
| `{{ marker }}` | `prb:<key>` (visible) |
| `{{ summary }}` | PR-level review summary |
| `{{ evidence.whyNewInThisPr }}` | nested |
| `{{ evidence.whyNotIntentional }}` | nested |
| `{{ evidence.contextFilesRead }}` | list — pipe through `join_list` |
| `{{ evidence.changedLines }}` | list — pipe through `join_list` |

Three custom filters are exposed:

* `join_list(value, sep=", ")` — join a list; `None` becomes `""`.
* `fence(value, language="")` — wrap in a fenced code block. Width
  adapts to the body: if the body already contains a run of backticks,
  the fence uses one more.
* `fence_lang(value, language)` — alias for `fence` with explicit lang.

Minimal example (`./pr-comment.md`):

```markdown
## {{ severity_label }} — {{ title }}

{{ message }}

{% if suggestion %}
**Suggested change**

{{ suggestion | fence }}
{% endif %}

{% if evidence.contextFilesRead %}
**Files read:** {{ evidence.contextFilesRead | join_list(", ") }}
{% endif %}
```

### Invariants the formatter enforces

* The dedupe marker (`<!-- prb:<key> -->`) is **always** the last line
  of the rendered body, on its own line, regardless of template
  content. The regex `^prb:([a-zA-Z0-9]{6,32})$` in `posting.py`
  relies on this for idempotent re-runs.
* If the template inlines `{{ marker }}` (or hand-writes a marker
  line), the formatter strips it and re-appends the canonical form.
* Output is truncated to `max_chars - 64` so the marker line always
  fits.
* `COMMENT_TEMPLATE_PATH` pointing at a missing file is a hard error
  (`ConfigError`). It does **not** silently fall back to the default
  — that would make typos look like "comments are formatted
  normally" when in fact the user expected a different layout.
