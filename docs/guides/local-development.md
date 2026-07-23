# Local development

**Purpose:** make and verify repository changes. **Audience:** contributors. **Mode:** how-to.

Install editable development dependencies, then run:

```bash
pytest -q
pytest --cov=reviewforge --cov-report=term-missing
```

Run the package entry point with `python -m reviewforge`; the installed console script is `reviewforge`. Keep source under `src/`, tests under `tests/`, prompts under `prompts/`, and standards under `standards/`. Read [testing](../development/testing.md), [code style](../development/code-style.md), and [review process](../development/review-process.md) before opening a change.

## Container operations on Linux and macOS

No PowerShell is required. With Docker or Podman installed, preview commands
without spawning a container:

```bash
python -m reviewforge.ops build --runtime docker --dry-run
python -m reviewforge.ops run --runtime docker --dry-run --print-command \
  --env-file .env --pr-url https://dev.azure.com/example/project/_git/repo/pullrequest/1 \
  --ado-token placeholder
```

Use `podman` in place of `docker` when appropriate. `--env-file` is forwarded
unchanged when it exists; explicit flags override process environment values.
