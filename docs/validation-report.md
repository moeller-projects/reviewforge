# Documentation validation report

**Purpose:** record checks run against the regenerated docs. **Audience:** maintainers and reviewers. **Mode:** reference.

## Passed

- Primary CLI help executed with `.venv/bin/python -m reviewforge --help`.
- `review`, `post`, `discover`, and `validate-config` help executed successfully.
- Documented primary command names and options match the parser output.
- Internal Markdown links were checked with a repository script after report generation; zero broken relative links were found.
- Prompt filenames were checked against the `prompts/` inventory and `Config` path fields.
- Artifact filenames were copied from `ARTIFACT_NAMES` in `artifacts/manager.py`.
- Pipeline names and stage lists were copied from `pipeline/stages/__init__.py`.
- Reasoning engine names were copied from the engine registry modules.
- Schema names and literal values were copied from `pipeline/schemas.py`.
- Documentation contract check found 33 Markdown files and purpose tags on all generated docs.
- Stage test module `.venv/bin/pytest tests/test_stages.py -q` passed.
- Full `uv run pytest tests/ --disable-warnings` passed: 820 passed, 1 skipped.
- Coverage gate passed: `uv run pytest tests/ --cov=reviewforge --cov-fail-under=97` reported 97.13% total coverage.

## Scope limits

- No live Azure DevOps review was run because it requires repository credentials, a real PR, and the external `pi` executable.
- No claim is made about deployment behavior beyond the checked-in CI and Azure Pipelines configuration files.
