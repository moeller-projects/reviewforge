# Running reviews

**Purpose:** choose and execute a review mode. **Audience:** operators. **Mode:** how-to.

Generate and post in one command:

```bash
reviewforge review
```

Generate without posting:

```bash
reviewforge review --no-post
reviewforge review --dry-run
```

Post an existing JSON document:

```bash
reviewforge post --input /path/to/final-findings.json
```

Validate configuration without a review:

```bash
reviewforge validate-config
```

`--force-review` bypasses draft/status/branch skip policy. `--force-full-review` ignores review history. `--reasoning-engine single_pi|multi_stage` selects the engine. `--output` copies the final review document to a caller-selected path. See [CLI](../reference/cli.md) and [artifacts](../reference/artifacts.md).

## Native engine (Codex subscription auth)

`REASONING_ENGINE=native` runs the review in-process with pydantic-ai, authenticating through the OpenAI Codex/ChatGPT subscription instead of an API key. Log in once:

```bash
codex login
```

Then run:

```bash
REASONING_ENGINE=native reviewforge review --dry-run
```

No `OPENAI_API_KEY` is required. To use an API-key model instead, set `NATIVE_MODEL` to any pydantic-ai model string (e.g. `openai:gpt-5.5`, `anthropic:claude-sonnet-4-5`). `NATIVE_CREDENTIAL_PATH` overrides the credential file location. See [environment variables](../reference/environment-variables.md) for the full native knob set.
