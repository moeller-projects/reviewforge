## Context

`ReviewResult` is canonical; historical fragment files only support legacy multi-stage diagnostics.

## Goals / Non-Goals

**Goals:** post from context, retire stable fragments, retain opt-in debug evidence.

**Non-Goals:** alter final-findings shape or posting markers.

## Decisions

- Fragment paths move under `raw/` and multi-stage removes them unless debugging is enabled.
- Post-only holds its supplied projection on `StageContext` and never seeds fragment files.

## Risks / Trade-offs

- Legacy external readers must migrate to `review-result.json` or `final-findings.json`.
