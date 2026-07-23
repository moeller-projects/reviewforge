## Context

The Python pipeline computes the authoritative merge-base unified diff but single_pi reduces oversized input mid-hunk. The preparation stage already persists commit subjects.

## Goals / Non-Goals

**Goals:** retain complete file-boundary chunks, supply bounded commit intent evidence, and merge validated partial results deterministically in one Pi session.

**Non-Goals:** ask Pi for Git history, change Pi's read-only tool allowlist, or change the canonical `ReviewResult` contract.

## Decisions

- Use the existing `diff --git ` boundaries and source order for deterministic partitions.
- Use a dedicated partial result schema, then synthesize and validate one canonical result in Python.
- Keep small diffs on the existing one-call path.

## Risks / Trade-offs

A single file section larger than the configured budget remains a practical context ceiling; normal configured budgets exceed individual file diffs.
