# Local development

**Purpose:** make and verify repository changes. **Audience:** contributors. **Mode:** how-to.

Install editable development dependencies, then run:

```bash
pytest -q
pytest --cov=reviewforge --cov-report=term-missing
```

Run the package entry point with `python -m reviewforge`; the installed console script is `reviewforge`. Keep source under `src/`, tests under `tests/`, prompts under `prompts/`, and standards under `standards/`. Read [testing](../development/testing.md), [code style](../development/code-style.md), and [review process](../development/review-process.md) before opening a change.
