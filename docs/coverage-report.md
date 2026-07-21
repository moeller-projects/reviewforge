# Documentation coverage report

**Purpose:** record implementation surfaces covered by the regenerated docs. **Audience:** maintainers and reviewers. **Mode:** reference.

- **Architecture:** covered in `docs/architecture/overview.md`, `pipeline.md`, `reasoning-engine.md`, `ai.md`, `configuration.md`, `artifacts.md`, and `extension-points.md`.
- **Modules:** covered in `repository-structure.md`; major package boundaries under `src/reviewforge/` are listed.
- **Configuration:** covered in `reference/configuration.md` and `environment-variables.md`, including source precedence, aliases, defaults, paths, engine, posting, context, AC, and session fields.
- **CLI:** covered in `reference/cli.md`; primary and legacy helper parsers are documented from current argparse definitions.
- **Schemas:** covered in `reference/schemas.md`; canonical `ReviewResult`, rich findings, legacy review docs, stage models, enums, and validators are named.
- **Prompts:** covered in `reference/prompts.md` and `guides/prompt-development.md`; all nine shipped prompt files are indexed.
- **Artifacts:** covered in `reference/artifacts.md` and `architecture/artifacts.md`; all 18 `ARTIFACT_NAMES` entries are listed.
- **Extension points:** covered in `architecture/extension-points.md` and `reference/public-api.md`; reasoning engines, stages, prompts, ADO client, and projections are described. No undocumented plugin mechanism is claimed.
- **Testing and metrics:** covered in `development/testing.md`, `development/benchmarking.md`, `development/performance.md`, and `reference/metrics.md`.

No historical documentation was present outside OpenSpec records, prompts, standards, and cache files. No material was moved to `docs/archive/`.
