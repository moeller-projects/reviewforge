# Troubleshooting

**Purpose:** diagnose common runtime failures. **Audience:** operators. **Mode:** how-to.

- **Missing configuration:** run `reviewforge validate-config`; set required ADO values or pass their CLI overrides.
- **Missing prompt/standards file:** inspect configured `*_PROMPT_PATH` and `REVIEW_STANDARDS_PATH`; `Config.validate_files()` reports the first missing path.
- **Missing executable:** the full pipeline requires `git`, `pi`, and `rg` on `PATH`.
- **Invalid model JSON:** inspect the stage artifact and Pi stderr; `PiRunner` performs one same-session JSON repair call before failing.
- **Stale session state:** use `--pi-session-clear`, or disable reuse with `--no-pi-session` for deterministic reruns.
- **No posting:** confirm `--dry-run`, `--no-post`, review skip policy, and `run-summary.json` before debugging ADO credentials.
- **Inline comment not mapped:** file findings require a valid diff mapping; fileless findings are posted as PR-level comments.

Artifacts and stage records are the first diagnostic source. See [artifacts](../reference/artifacts.md).
