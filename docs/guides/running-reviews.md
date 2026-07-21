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
