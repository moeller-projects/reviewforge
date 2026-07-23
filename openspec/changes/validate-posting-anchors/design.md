## Context

`DiffLineMapper` already provides the authoritative new-side line sets for the Python-computed diff. Posting must not receive model-invented anchors.

## Goals / Non-Goals

**Goals:** validate projected findings before posting, preserve work-item general comments, and record deterministic outcomes.

**Non-Goals:** call a model, alter the PR diff source, or change posting marker layout.

## Decisions

Use `DiffLineMapper.line_set`; downgrade by clearing anchors or drop by policy. Mirror dropped canonical findings as discarded findings.

## Risks / Trade-offs

General comments preserve review evidence when an inline location is invalid but lose file placement.