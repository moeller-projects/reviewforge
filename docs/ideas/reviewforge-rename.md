# ReviewForge rename proposal

## Purpose

Capture the work required to rename this repository and its user-facing product identity to **ReviewForge**.

## Audience

Maintainers planning the repository, package, CLI, container, and documentation rename.

## Proposed identity

| Surface | Current | Proposed |
|---|---|---|
| GitHub repository | `auto-pr-reviewer` | `reviewforge` |
| Python package | `auto_pr_reviewer` | `reviewforge` |
| Python distribution | `auto-pr-reviewer` | `reviewforge` |
| Console command | `auto-pr-reviewer` | `reviewforge` |
| Python module invocation | `python -m auto_pr_reviewer` | `python -m reviewforge` |
| Container image | `pr-review-bot` | `reviewforge` |
| Container name prefix | `pr-review-bot-pr-<PR_ID>` | `reviewforge-pr-<PR_ID>` |
| Artifact volume | `pr-review-bot-artifacts` | `reviewforge-artifacts` |
| Visible product name | `PR Review Bot` | `ReviewForge` |

## Scope of changes

### 1. Repository identity

Update the GitHub repository slug, title, description, README branding, badges, clone URLs, issue links, and any CI or deployment references to the old repository name.

Update root-level references in:

- `README.md`
- `AGENTS.md`
- `Dockerfile`
- `azure-pipelines-pr-review.yml`
- PowerShell script comments and help text

### 2. Python package and distribution

Rename:

```text
src/auto_pr_reviewer/
```

to:

```text
src/reviewforge/
```

Update every internal import, test import, monkeypatch target, module reference, and package-data declaration.

Update `pyproject.toml`:

- distribution name: `auto-pr-reviewer` → `reviewforge`
- console script: `auto-pr-reviewer` → `reviewforge`
- package-data key: `auto_pr_reviewer` → `reviewforge`

Update coverage commands from:

```bash
--cov=auto_pr_reviewer
```

to:

```bash
--cov=reviewforge
```

Affected implementation and compatibility surfaces include:

- `src/auto_pr_reviewer/`
- `scripts/main.py`
- `scripts/review.py`
- `scripts/ado_review.py`
- all tests under `tests/`
- `Dockerfile`
- `Dockerfile.tests`

### 3. CLI branding

Update the CLI program name and help text in `src/auto_pr_reviewer/cli.py`.

Change documented commands such as:

```bash
python -m auto_pr_reviewer review
```

to:

```bash
python -m reviewforge review
```

Update command examples for `review`, `post`, `discover`, `open-prs`, and `validate-config`.

PowerShell wrappers can retain their existing file paths, but their help text and comments should use ReviewForge branding.

### 4. Container and runtime names

Update runtime defaults and documentation for:

```text
pr-review-bot:latest → reviewforge:latest
pr-review-bot-pr-<PR_ID> → reviewforge-pr-<PR_ID>
pr-review-bot-artifacts → reviewforge-artifacts
```

Affected files include:

- `.env.example`
- `build.ps1`
- `Dockerfile`
- `azure-pipelines-pr-review.yml`
- `README.md`
- `docs/reference/configuration.md`
- `docs/handoff-end-user.html`
- `docs/onboarding-team.html`

Changing the named volume creates a new volume. Existing artifacts in `pr-review-bot-artifacts` require an explicit migration or continued mounting of the old volume.

### 5. Documentation branding

Update current documentation, including:

- `docs/reference/README.md`
- `docs/reference/package-guide.md`
- `docs/reference/cli.md`
- `docs/reference/configuration.md`
- `docs/reference/pipeline.md`
- `docs/reference/ado-integration.md`
- `docs/reference/ai-runner.md`
- `docs/reference/artifacts.md`
- `docs/design/architecture.md`
- `docs/handoff-end-user.html`
- `docs/onboarding-team.html`

Replace current product, package, image, volume, path, and command references consistently.

Historical documents under `docs/archive/` should remain historically accurate. Add a short rename note only where useful; do not rewrite historical migration documents as if they originally used the ReviewForge name.

## Compatibility decisions

### Legacy shim paths

Keep `scripts/ado_review.py` unless all external callers are known and migrated. This filename represents a legacy ADO helper and is not required to change with the product name.

Its imports should point to the new package:

```python
from reviewforge.ado.legacy import main as legacy_main
```

If external consumers import `auto_pr_reviewer`, choose one of two approaches:

- **Clean cutover:** remove the old package name and update every caller.
- **Compatibility release:** retain a temporary forwarding package named `auto_pr_reviewer`.

The repository's default preference is a clean cutover unless downstream consumers require compatibility.

### ADO environment variables

Keep integration contract names unchanged:

- `ADO_ORG`
- `ADO_PROJECT`
- `ADO_REPO_ID`
- `ADO_AUTH_TOKEN`
- `PR_ID`
- `PR_URL`
- `REVIEW_ARTIFACT_DIR`

These describe Azure DevOps or review behavior, not the product brand.

### Artifact paths

Preserve the artifact layout:

```text
artifacts/pr-<PR_ID>/runs/<RUN_ID>/
```

`ARTIFACT_NAMES` is a stable contract and should not be changed as part of the rename.

### Idempotency marker

Do **not** rename the persisted `prb:<key>` marker as part of the basic branding change.

The marker is already stored in Azure DevOps comments and is used for deduplication. Changing it to a new prefix would make existing comments invisible to the scanner and could repost every prior finding.

Recommended behavior:

- Keep `prb:<key>` for existing and new comments.
- Update product-facing text to ReviewForge.
- Document `prb` as the historical persistence marker.

If the marker must change later, the implementation must recognize both old and new prefixes while writing only the new prefix. That is a separate behavior migration and requires release notes and regression coverage.

## Verification

After implementation, run the narrow entrypoint checks:

```bash
python -m reviewforge --help
python scripts/main.py --help
python scripts/review.py --help
python scripts/ado_review.py --help
```

Run the coverage gate:

```bash
pytest tests/ --cov=reviewforge --cov-fail-under=95
```

Finally, search for unintended leftovers:

```text
auto_pr_reviewer
auto-pr-reviewer
pr-review-bot
PR Review Bot
```

Expected remaining references should be limited to intentional historical notes, migration documentation, compatibility aliases, or the preserved `prb:` marker contract.

## Recommended implementation shape

Make the rename as one coordinated change covering:

1. package paths and imports;
2. packaging metadata and CLI entrypoints;
3. tests and coverage configuration;
4. image, container, and volume defaults;
5. current documentation and operator-facing text;
6. repository metadata and CI references.

Preserve artifact paths, Azure DevOps environment variables, legacy shim filenames, and the `prb:` deduplication marker unless a separate migration plan explicitly changes those contracts.
