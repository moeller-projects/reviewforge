# Production-grade PR review workflow

## Purpose

This document describes the target design for a production-grade automated Azure DevOps PR reviewer.

The current reviewer is too diff-first: it can identify obvious problems, but it may miss author intent, project conventions, linked requirements, and surrounding code context. The redesigned workflow should behave more like a careful human reviewer: understand the change first, gather relevant context, then only post findings that survive adversarial scrutiny.

## Core principle

Do not post a finding unless the reviewer can explain:

1. what the PR is trying to accomplish,
2. which surrounding code or requirements were checked,
3. why the finding is newly introduced by this PR,
4. why the issue is not plausibly intentional, and
5. why the finding is worth interrupting the author.

The workflow should move from:

```text
diff -> findings
```

to:

```text
PR metadata + diff
-> intent reconstruction
-> context collection plan
-> context digest
-> candidate findings
-> adversarial verification
-> final findings
-> post/vote
```

## Stage 1: PR discovery

For each PR, collect metadata before invoking any reviewer model.

Inputs:

- PR ID
- project
- repository ID/name
- source branch
- target branch
- title
- description
- status
- `isDraft`
- author
- reviewers
- commit messages
- linked work items
- work item comments
- existing PR threads

Skip PRs that are not reviewable:

- `isDraft: true`
- status is not `active`
- target branch is outside the configured target branch list
- already reviewed according to configured policy, if that policy is enabled

Expected artifact:

```json
{
  "pr": {},
  "commits": [],
  "workItems": [],
  "workItemComments": [],
  "existingThreads": []
}
```

## Stage 2: Repository preparation

Prepare the repository locally so the reviewer can inspect real code, not only the patch.

Checkout or fetch:

- target branch
- source branch
- merge base

Generate:

```bash
git diff merge-base..source
git diff --name-status merge-base..source
git log --oneline merge-base..source
```

For each changed file, record metadata:

```json
{
  "file": "src/example.ts",
  "status": "modified",
  "addedLines": 42,
  "deletedLines": 12,
  "isTest": false,
  "language": "typescript"
}
```

Expected artifacts:

```text
artifacts/pr-<id>/diff.patch
artifacts/pr-<id>/changed-files.json
artifacts/pr-<id>/commits.txt
```

## Stage 3: Intent reconstruction

Before looking for issues, run an intent-only pass.

The model must not produce findings in this phase. Its job is to infer the intended change from PR metadata, linked work items, commit messages, and changed file summary.

Expected output:

```json
{
  "pr_intent": "...",
  "requirements": [],
  "changed_behaviors": [],
  "risk_areas": [],
  "files_requiring_context": [],
  "unclear_areas": []
}
```

This stage answers:

- What is the author trying to change?
- Which requirements appear relevant?
- Which behavior is intentionally different?
- Which areas are risky enough to require deeper inspection?

## Stage 4: Context collection plan

The reviewer then creates a concrete plan for context gathering.

Expected output:

```json
{
  "files_to_read": [
    {
      "path": "src/orders/service.ts",
      "reason": "Changed validation path affects order creation."
    }
  ],
  "symbols_to_trace": [
    {
      "symbol": "calculateQuantity",
      "reason": "Changed quantity behavior may affect totals."
    }
  ],
  "tests_to_inspect": [
    "tests/orders/service.test.ts"
  ],
  "searches_to_run": [
    {
      "query": "calculateQuantity",
      "reason": "Find call sites and related behavior."
    }
  ]
}
```

The runner should execute this plan using read-only tools.

Allowed context operations should include:

- read files
- grep/search code
- inspect related tests
- inspect call sites when code navigation is available
- inspect project conventions near the changed code

## Stage 5: Context digest

Raw context can become too large. After collection, compress it into a review-oriented digest.

Expected output:

```json
{
  "relevant_context": [
    {
      "file": "src/orders/service.ts",
      "summary": "Order quantity is normalized before persistence.",
      "important_lines": [44, 71, 103]
    }
  ],
  "project_conventions": [],
  "existing_tests": [],
  "possible_intentional_choices": [],
  "context_gaps": []
}
```

This digest becomes part of the evidence basis for findings.

## Stage 6: Candidate finding generation

Only after intent and context are available should the reviewer generate candidate findings.

Each candidate must include evidence, not only a claim.

Expected output:

```json
{
  "summary": "...",
  "findings": [
    {
      "file": "src/orders/service.ts",
      "line": 88,
      "severity": "major",
      "title": "...",
      "claim": "...",
      "evidence": {
        "changed_lines": [],
        "context_files_read": [],
        "why_new_in_this_pr": "...",
        "why_not_intentional": "..."
      },
      "suggestion": "...",
      "confidence": "high"
    }
  ]
}
```

Reject candidate findings automatically if they do not include:

- changed file and changed line where possible,
- why the issue is introduced or exposed by the PR,
- which context was read,
- why the issue is not simply intentional behavior,
- confidence level.

## Stage 7: Adversarial verification

Run a second pass that defends the PR author.

Verifier prompt goal:

> Try to refute this finding using the PR intent, linked requirements, existing comments, surrounding code, and project conventions.

Expected output:

```json
{
  "verified_findings": [
    {
      "finding_id": "...",
      "verdict": "keep",
      "final_severity": "major",
      "reason": "The surrounding code confirms this path persists invalid state.",
      "missing_context": []
    }
  ]
}
```

Allowed verdicts:

- `keep`
- `drop`
- `downgrade`

Drop or downgrade findings when:

- the finding depends on speculation,
- the author intent plausibly explains the change,
- linked requirements support the behavior,
- surrounding code refutes the claim,
- the issue is pre-existing and not made worse by the PR,
- the finding is merely a stylistic preference,
- confidence is low.

## Stage 8: Severity calibration

Severity should be calibrated separately from finding generation.

Recommended definitions:

| Severity | Meaning |
| --- | --- |
| `blocker` | likely production bug, data loss, security issue, or clear merge blocker |
| `major` | should be fixed before merge |
| `minor` | useful but non-blocking improvement |
| `nit` | normally suppressed unless explicitly requested |

Recommended production defaults:

```text
POST_MIN_SEVERITY=major
VOTE_WAITING_ON=major
DROP_LOW_CONFIDENCE=true
```

This keeps the bot high-signal.

## Stage 9: Existing-comment deduplication

Before posting, compare accepted findings against existing PR threads.

Rules:

- Do not post duplicate findings.
- If the same issue already exists, suppress it.
- If new evidence is useful, consider appending to the existing thread instead of creating a new one.
- Ignore resolved or closed threads only if policy explicitly allows re-raising.

## Stage 10: Final output and posting

Final review output should contain both findings and review basis.

Expected final JSON:

```json
{
  "summary": "...",
  "review_basis": {
    "intent_understood": true,
    "files_read": [],
    "work_items_checked": [],
    "existing_comments_checked": true,
    "verification_passed": true
  },
  "findings": []
}
```

Posting layer responsibilities:

- validate final schema,
- post accepted findings only,
- suppress findings below configured severity,
- avoid duplicates,
- vote `waiting for author` only when accepted findings meet `VOTE_WAITING_ON`,
- write artifacts for auditability.

## Large PR handling

Large PRs should not be reviewed as isolated file chunks without global context.

Preferred approach:

1. Build global PR intent and requirements once.
2. Build global changed-file inventory.
3. Split review by subsystem or related file group, not arbitrary file count.
4. Pass the global intent digest to every chunk.
5. For each chunk, enforce the same evidence and verification rules.
6. Merge findings and deduplicate globally.

Chunk reviews must not invent missing-file findings based on files outside the chunk.

## Audit artifacts

Every PR review should produce durable artifacts.

Suggested layout:

```text
artifacts/pr-8388/
  metadata.json
  diff.patch
  changed-files.json
  commits.txt
  intent.json
  context-plan.json
  context-digest.json
  candidate-findings.json
  verified-findings.json
  final-findings.json
  posted-findings.json
```

These artifacts make false positives debuggable and allow prompt/workflow improvements based on evidence.

## Recommended implementation structure

Use separate prompts or stages instead of one large prompt:

```text
prompts/intent.md
prompts/context-plan.md
prompts/context-digest.md
prompts/findings.md
prompts/verify-findings.md
prompts/severity.md
```

The runner should orchestrate the stages and validate JSON after each one.

## Failure handling

Each stage should fail cleanly.

Recommended behavior:

- invalid JSON: retry once with a repair prompt,
- missing context file: record in `context_gaps`, do not invent conclusions,
- model timeout: fail review with diagnostic artifact,
- posting failure: keep final findings artifact and return non-zero,
- vote failure: log explicitly; optionally fail based on policy.

## Production policy defaults

Recommended initial production configuration:

```text
INCLUDE_WORK_ITEMS=true
INCLUDE_EXISTING_COMMENTS=true
VERIFY_FINDINGS=true
REQUIRE_CONTEXT_FOR=minor,major,blocker
POST_MIN_SEVERITY=major
VOTE_WAITING_ON=major
DROP_LOW_CONFIDENCE=true
MAX_FINDINGS=10
```

## Success criteria

The redesigned reviewer is successful when:

- findings are rare but high-confidence,
- authors can see why a finding is real,
- false positives caused by missing context decrease,
- every posted finding has an auditable evidence trail,
- the bot blocks only when it finds issues that would reasonably block a human review.
