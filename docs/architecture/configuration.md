# Configuration architecture

**Purpose:** explain configuration sources and precedence. **Audience:** operators and contributors. **Mode:** explanation.

`Config.from_sources(cli, env=...)` builds the frozen dataclass from CLI overrides layered over environment values. `Config.from_env_file()` adds a lowest-priority `.env` layer: CLI > process environment > `.env`. `Config.from_env()` is the legacy environment-only constructor.

Environment aliases are resolved per logical key; token lookup accepts `SYSTEM_ACCESSTOKEN`, `ADO_AUTH_TOKEN`, `ADO_MCP_AUTH_TOKEN`, and `ADO_API_KEY`. PR identity accepts `PR_ID` or `PR_URL`. Prompt paths default to `/app/prompts/...` and can be overridden. Full names and defaults are in the [configuration reference](../reference/configuration.md).

`Config.validate_files()` checks standards and the prompt set required by the selected engine. `validate_for_command()` reports missing ADO identity values for command-specific use. CLI values override environment values, and boolean flags with explicit positive/negative forms take precedence over `DRY_RUN`-style environment values.
