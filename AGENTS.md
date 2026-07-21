# AGENTS.md

## Purpose

This is the working guide for agents changing ReviewForge. The implementation under `src/reviewforge/` is authoritative; do not infer behavior from older documentation or OpenSpec history.

## Architecture

ReviewForge has a physical pipeline in `src/reviewforge/pipeline/`: fetch PR metadata, prepare a Git checkout and diff, execute a registered reasoning engine, then post or retain findings. `ReviewResult` in `pipeline/schemas.py` is the canonical engine output. Legacy JSON files are projections. See [architecture](docs/architecture/overview.md).

The production default is `single_pi`; `multi_stage` runs the legacy intent/plan/context/review/verify/calibrate flow. Engines do not automatically fall back to one another: the configured engine must succeed or the stage fails. Pi calls are made through `ai.runner.PiRunner`, which scrubs ADO credentials from the subprocess environment, restricts review interaction to read-only tools, and can reuse a per-PR session. Pi must never receive ADO/network side effects; authenticated ADO requests belong in `reviewforge.ado`.

## Repository conventions

- Python package code belongs under `src/reviewforge/`.
- Tests belong under `tests/` and defend observable behavior.
- Prompts belong under `prompts/`; coding standards belong under `standards/`.
- Keep public exports and JSON shapes compatible unless the change explicitly changes the contract.
- Use Pydantic schemas and immediate validation for model-produced JSON.
- Keep secrets out of artifacts and Pi subprocess environments.
- Preserve `ARTIFACT_NAMES` filenames and meanings. Treat the list as a stable compatibility contract; do not rename, remove, or repurpose entries without an explicit migration.
- Preserve the `prb:<dedupe-key>` marker and marker layout in posted comments. Posting uses existing markers to avoid duplicate threads on reruns.

## Coding standards

Prefer small, direct functions and existing helpers. Preserve error handling at trust boundaries: CLI/env parsing, Git, Pi output, JSON validation, and Azure DevOps posting. Avoid speculative abstractions, new dependencies, and compatibility shims that have no current caller.

## Prompt standards

Prompts are data contracts, not prose-only configuration. Preserve JSON-only output requirements, scope limits, untrusted-content handling, evidence requirements, and current field names. Runtime composition is handled by `ai.prompts`; verify any changed prompt path with `Config.validate_files()` and the relevant engine.

## Testing expectations

Run the narrowest relevant test first, then the full suite for permanent behavior changes:

```bash
pytest -q
pytest --cov=reviewforge --cov-report=term-missing
```

Tests cover CLI parsing, configuration, stages, reasoning, ADO behavior, posting, stale reconciliation, session reuse, and entry points. A change is not complete if the implementation works only through an untested happy path.

## Review workflow

1. Inspect the current implementation and callers before editing.
2. Preserve the canonical `ReviewResult` path and project only at boundaries.
3. Update tests for new observable behavior.
4. Run the changed-path test and a smoke command.
5. Update docs only from verified implementation behavior.
6. Validate OpenSpec artifacts when a behavior change has an active change record.

## Documentation standards

Use Markdown for repository docs. Keep tutorial, how-to, reference, and explanation content separate. State purpose and audience, verify commands and paths, cross-link instead of duplicating, and list gaps explicitly. Do not rewrite existing top-level docs wholesale without explicit user approval; this task provides that approval.

## Useful entry points

- `python -m reviewforge` or `reviewforge`: primary CLI.
- `reviewforge review`: generate and normally post a review.
- `reviewforge post --input <file>`: post an existing final-findings document.
- `reviewforge discover --project <name>`: emit active PRs as JSON.
- `python -m reviewforge.ado.cli fetch-context ...`: legacy helper CLI.
- `python -m reviewforge.ado.cli post-findings ...`: legacy posting helper.
