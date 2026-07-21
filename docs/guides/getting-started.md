# Getting started

**Purpose:** run a local dry-run review. **Audience:** new contributors and operators. **Mode:** tutorial.

## Install

```bash
python -m venv .venv
. .venv/bin/activate
python -m pip install -e '.[dev]'
```

Install `git`, `rg`, and the `pi` CLI separately. The package declares only `pydantic` and `jinja2` as runtime dependencies.

## Configure

Set `ADO_ORG`, `ADO_PROJECT`, `ADO_REPO_ID`, `PR_ID`, and `ADO_AUTH_TOKEN`. Ensure the configured prompt and standards paths exist. See [environment variables](../reference/environment-variables.md).

## Run without posting

```bash
reviewforge review --dry-run
```

The command creates a per-run artifact directory and writes `final-findings.json`, `review-result.json`, and `run-summary.json`. Use [running reviews](running-reviews.md) for posting and follow-up options.
