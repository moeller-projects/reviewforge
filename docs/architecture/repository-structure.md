# Repository structure

**Purpose:** locate implementation surfaces. **Audience:** contributors. **Mode:** reference.

- `src/reviewforge/`: installable package.
  - `cli.py`, `__main__.py`: primary command-line entry points.
  - `config.py`: CLI/env/.env resolution and validation.
  - `pipeline/`: contexts, stages, orchestration, projections, schemas, validation.
  - `reasoning/`: `ReasoningEngine`, `single_pi`, and `multi_stage` implementations.
  - `ai/`: Pi subprocess runner and prompt composition.
  - `ado/`: REST client, helper CLI, diff mapping, comments, voting, and posting.
  - `git/`: authenticated clone, merge-base diff, and cleanup.
  - `artifacts/`: run directory paths, JSON helpers, and summaries.
- `tests/`: pytest tests for CLI, stages, reasoning, ADO, posting, sessions, and entry points.
- `prompts/`: system prompts used by the reasoning engines and optional AC coverage pass.
- `standards/`: coding standards injected into review prompts.
- `openspec/`: active and archived behavior-change records.
- `.github/workflows/python-tests.yml`: CI test workflow.
- `azure-pipelines-pr-review.yml`: Azure Pipelines integration configuration.

The package is built with setuptools from `src/`, requires Python 3.11+, and declares `reviewforge = reviewforge.cli:main` in `pyproject.toml`.
