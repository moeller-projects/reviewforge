## Why

ReviewForge currently invokes the reasoning engine without deterministic knowledge of prior reviews, so reruns can repeat work and comments. The orchestration layer should skip unchanged reruns and narrow follow-up reviews without asking Pi to infer review history.

## What Changes

- Add a normalized, deterministic review-state model and strongly typed review modes.
- Resolve the authenticated ADO identity once per run and classify that reviewer's prior comments and threads.
- Select Initial, FollowUp, NoOp, or ForceFull before prompt construction or reasoning execution.
- Use the last confidently reviewed commit as the lower bound for follow-up diffs; fall back to a full review when state is incomplete.
- Pass normalized prior findings, active/resolved comments, and follow-up metadata to the existing reasoning engines.
- Add `--force-full-review` as an explicit override.

## Capabilities

### New Capabilities

- `intelligent-review-mode`: Deterministic review-state discovery, mode selection, no-op behavior, and follow-up context optimization.

### Modified Capabilities

- None.

## Impact

The change affects ADO context fetching, pipeline orchestration, repository diff preparation, prompt context, CLI configuration, and related tests. The existing `ReasoningEngine` abstraction remains unchanged.
