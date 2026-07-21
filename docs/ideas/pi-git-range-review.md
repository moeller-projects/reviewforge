# Pi git-range review idea

## Purpose

Capture an alternative review architecture in which Python selects and validates the commit range while Pi discovers the diff and commit impact from the checked-out repository.

## Audience

Maintainers evaluating whether ReviewForge should move repository-diff discovery from deterministic Python code into the Pi reasoning process.

## Idea

Instead of passing the complete changed-file list and unified diff into every reasoning prompt, the orchestration layer would pass Pi:

- the checked-out repository path or working directory;
- the validated review mode;
- the validated commit range;
- the current source and target commits;
- compact PR and prior-review context.

Pi would then use read-only repository tools to run commands equivalent to:

```bash
git log <review-range>
git diff <review-range>
```

and analyze the impact of those changes, including cross-file consequences and nearby code.

## Recommended boundary

Do not make Pi unrestricted. The repository currently requires Pi to remain read-only and prevents the model from making Azure DevOps calls.

The safe version of this idea would provide a narrowly scoped read-only git/shell capability that:

- permits `git log`, `git diff`, and read-only repository inspection;
- denies file writes, network access, and Azure DevOps commands;
- runs against the temporary repository prepared for the current review;
- receives the exact Python-validated range rather than choosing its own base and head;
- preserves JSON-only output validation and the existing `ReasoningEngine` abstraction.

Removing `--tools read,grep` without replacing it with an explicit read-only boundary would be unsafe and would violate the current Pi execution contract.

## Proposed flow

```text
ADO review history
        ↓
Python selects Initial / FollowUp / NoOp / ForceFull
        ↓
Python validates commit ancestry and selects range
        ↓
Python prepares temporary repository
        ↓
Pi receives repository + validated range + review context
        ↓
Pi runs read-only git inspection and analyzes impact
        ↓
Python validates and posts structured findings
```

For a follow-up review, Python would still select:

```text
last-reviewed-commit..current-source-commit
```

If the previous commit is unavailable or is not an ancestor, Python would fall back to the normal full range before invoking Pi.

## Why consider it

- Pi can inspect commit history and determine impact across files without Python serializing the entire diff into every prompt.
- The model can request surrounding files only when relevant.
- Large or multi-file changes may get more natural repository-level analysis.
- Commit messages and change history become first-class review evidence.

## Costs and risks

- Tool calls make token usage, latency, and behavior less predictable.
- Pi must run with the prepared repository as its working directory or receive a safe repository path.
- Git output must be bounded to prevent oversized prompts and runaway inspection.
- Tool failures, missing commits, shallow clones, rebases, and force-pushes need deterministic fallback behavior.
- Unrestricted shell access could modify files, exfiltrate data, or reach Azure DevOps.
- Replacing Python-generated diffs entirely would make NoOp and follow-up guarantees harder to prove.

## Minimal implementation shape

1. Extend `PiRunner.run_json()` with an optional repository working directory and a narrowly scoped git-read tool configuration.
2. Pass the already validated review range through `StageContext`.
3. Update the system prompts to require review of exactly that range.
4. Let Pi fetch `git log` / `git diff` through the read-only capability, with explicit output limits.
5. Keep Python mode detection, ancestry checks, NoOp short-circuiting, JSON validation, deduplication, and all ADO side effects unchanged.
6. Compare token usage and latency against the current Python-generated-diff approach before adopting it as the default.

## Recommendation

Treat this as an experiment or opt-in mode first. Keep Python-generated diff context as the default until measurements show that repository-driven Pi inspection improves impact detection without unacceptable increases in latency, token usage, or operational risk.

## Verification requirements

A future implementation should prove:

- Pi cannot write files or call Azure DevOps.
- Pi receives and obeys the Python-selected range.
- NoOp invokes no Pi tools or reasoning calls.
- Follow-up reviews do not inspect commits before the validated lower bound.
- Missing or invalid history falls back to a full review.
- JSON output, deduplication, and existing posting behavior remain unchanged.
- Token usage and wall-clock latency are measured against the current approach.
