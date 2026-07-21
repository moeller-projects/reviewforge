# Release process

**Purpose:** describe the verified release surface. **Audience:** maintainers. **Mode:** how-to.

The package version is declared in `pyproject.toml`; builds use setuptools. Before release:

1. Run `pytest -q`.
2. Run coverage if the change affects behavior.
3. Validate docs and CLI examples against the current parser.
4. Review generated artifacts and prompt paths in a dry run.
5. Update `CHANGELOG.md` when a release entry is required.

CI currently runs from `.github/workflows/python-tests.yml`; Azure integration configuration is in `azure-pipelines-pr-review.yml`. This repository does not contain an implementation-specific publish command or release automation beyond those files, so deployment steps are intentionally not documented here.
