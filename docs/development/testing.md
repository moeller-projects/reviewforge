# Testing

**Purpose:** explain the current verification strategy. **Audience:** contributors. **Mode:** explanation.

Tests use pytest and are organized by observable surface: CLI and entry points, configuration, review mode, orchestration, stages, reasoning, Pi session reuse, ADO client/CLI, posting and comment formatting, acceptance-criteria coverage, stale reconciliation, and repository conventions.

Run the suite:

```bash
pytest -q
```

Run coverage:

```bash
pytest --cov=reviewforge --cov-report=term-missing
```

Add tests when a change introduces a new contract or boundary. Prefer deterministic tests of parsed arguments, schema validation, stage outcomes, artifact contents, and posting decisions over source-text assertions.
