# ReviewForge

> Azure DevOps pull-request review service driven by Pi and structured review schemas.

ReviewForge fetches one Azure DevOps pull request, prepares its merge-base diff, runs a selectable reasoning engine, validates the result, writes per-run artifacts, and optionally posts findings back to Azure DevOps.

## Quick start

Requires Python 3.11+, Git, `rg`, the `pi` CLI, and Azure DevOps access. The commands below use the repository's local prompt and standards files; container deployments use their mounted `/app/...` paths instead.

```bash
python -m venv .venv
. .venv/bin/activate
python -m pip install -e '.[dev]'
export ADO_ORG=example
export ADO_PROJECT=project
export ADO_REPO_ID=repository
export PR_ID=123
export ADO_AUTH_TOKEN='...'
export REVIEW_STANDARDS_PATH=standards/clean-code.md
export FAST_REVIEW_PROMPT_PATH=prompts/fast-review-system.md
reviewforge review --dry-run
```

Use `reviewforge -h` and `reviewforge <command> -h` for the authoritative CLI surface. Configuration precedence is documented in [Configuration](docs/reference/configuration.md).

## What runs

The default `review` path executes metadata fetch, repository preparation, the selected reasoning engine, and (unless dry-run/no-post applies) Azure DevOps posting. `single_pi` is the default engine; `multi_stage` is available for debugging, benchmarking, regression comparison, and explicit fallback selection. Engines do not automatically fall back to one another. See [Pipeline](docs/architecture/pipeline.md) and [Reasoning engine](docs/architecture/reasoning-engine.md).

## Documentation

- [Getting started](docs/guides/getting-started.md)
- [Running reviews](docs/guides/running-reviews.md)
- [Operator and scheduling workflows](docs/guides/operations.md)
- [Reference index](docs/reference/README.md)
- [Architecture overview](docs/architecture/overview.md)
- [CLI reference](docs/reference/cli.md)
- [Configuration reference](docs/reference/configuration.md)
- [Schema reference](docs/reference/schemas.md)
- [Artifact reference](docs/reference/artifacts.md)
- [Development and testing](docs/development/testing.md)
- [Coverage and validation reports](docs/coverage-report.md)

## Project layout

Implementation is under `src/reviewforge/`; tests are under `tests/`; runtime prompts are under `prompts/`; OpenSpec change artifacts are under `openspec/`. See [Repository structure](docs/architecture/repository-structure.md).
