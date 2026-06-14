Task: Add explicit configuration validation

You are working in an existing automated PR reviewer repository.

Improve configuration validation so commands fail fast with clear actionable errors.

Goals:
- Add command-specific config validation.
- Add a command:

python scripts/main.py validate-config

- Error messages should identify:
  - missing config key
  - command that requires it
  - how to set it, e.g. `.env`, environment variable, or CLI flag
- Validate aliases such as:
  - ADO_AUTH_TOKEN / ADO_MCP_AUTH_TOKEN
  - IMAGE_NAME / IMAGE
  - PR_ID / PR_URL
- Do not log secret values.
- Add tests for missing config, alias resolution, and validation success.

Example error style:

Missing required config: ADO_ORG
Required by command: review
Set it in .env or pass —ado-org.

Constraints:
- Preserve existing config behavior where possible.
- CLI args should override env/.env values.
- Azure Pipelines must continue working.
- Existing tests must pass.

Before editing, inspect the config implementation and produce a short validation plan.
Then implement incrementally.
