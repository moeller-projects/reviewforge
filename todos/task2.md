Task: Add a stable CLI contract

You are working in an existing automated PR reviewer repository.

Create or refine a stable command-line interface for the Python application.

Desired commands:

python scripts/main.py review
python scripts/main.py review —pr 12345
python scripts/main.py open-prs
python scripts/main.py validate-config

If existing commands differ, preserve backward compatibility through aliases or deprecated options.

Goals:
- Make command behavior predictable and documented.
- Ensure CLI args override environment/.env config.
- Keep dynamic values like PR ID available as CLI overrides.
- Add helpful `—help` text.
- Return clear non-zero exit codes on failure.
- Update README usage examples.
- Add tests for CLI parsing and config precedence.

Constraints:
- Do not remove existing behavior without compatibility aliases.
- Do not put business logic directly in the CLI parser.
- Azure Pipelines must continue working.
- Existing tests must pass.

Before editing, inspect the current CLI and produce a short compatibility plan.
Then implement the smallest safe changes.
