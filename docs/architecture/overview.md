# Architecture overview

**Purpose:** explain the current runtime shape. **Audience:** engineers and agents who need a system model. **Mode:** explanation.

ReviewForge is a Python package with four boundaries: CLI/configuration, Azure DevOps and Git integration, a physical pipeline, and Pi-backed reasoning. `pipeline.orchestrator` builds a `StageContext`, runs a fixed stage list, records `StageResult` values, and finalizes a redacted run summary.

The default flow is:

```text
CLI -> Config -> FetchPrMetadata -> PrepareRepository -> ReasoningEngine -> PostToAdo
                                      |                       |
                                      +-- per-run artifacts --+-- ReviewResult
```

`ReviewResult` is the canonical structured output. `pipeline.projection` converts it to the legacy `final-findings.json` shape consumed by posting and callers. Validation occurs before posting. The [pipeline reference](pipeline.md) lists the exact stage lists; [artifacts](artifacts.md) lists stable files.

External boundaries are `git`, `rg`, `pi`, and Azure DevOps REST APIs. The Pi runner removes ADO credential variables from its child environment. Configuration and CLI are described in [configuration](configuration.md) and [CLI reference](../reference/cli.md).
