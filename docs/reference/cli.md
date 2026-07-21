# CLI reference

**Purpose:** list current command-line interfaces. **Audience:** operators and scripts. **Mode:** reference.

## Primary CLI

`reviewforge` and `python -m reviewforge` invoke `reviewforge.cli:main`. With no arguments, the command defaults to `review`.

### Common options for `review`, `post`, `open-prs`, and `validate-config`

`--org`, `--project`, `--repo`, `--pr`, `--pr-url`, `--source-branch`, `--target-branch`, `--ado-token`, `--pi-model`, `--language`, `--review-artifact-dir`, `--review-run-id`, `--dry-run`, `--no-dry-run`, `--force-review`, `--force-full-review`, `--pi-session-id`, `--no-pi-session`, `--pi-session-clear`, `--fast-review`, `--reasoning-engine`.

### Commands

- `review [--output PATH] [--no-post]`: generate findings; posts by default unless disabled. Supplying `--output` selects review-only mode and does not post.
- `post --input PATH`: post a previously generated review JSON.
- `open-prs`: unsupported in this CLI; use `./run-open-prs.ps1` when present in the deployment environment.
- `validate-config`: validate configuration and exit.
- `discover --project NAME [--org NAME] [--ado-token TOKEN] [--target-branches LIST] [--max N]`: emit active pull requests as JSON.

## Legacy ADO helper

`python -m reviewforge.ado.cli` exposes `fetch-context --org O --project P --repo R --pr N --out PATH` and `post-findings --org O --project P --repo R --pr N --findings PATH --out PATH`.

Use `-h` on every command for parser-generated help; the parser is authoritative.
