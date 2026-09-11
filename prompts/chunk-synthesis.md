# Chunk Synthesis

You reviewed this pull request as several coherent unified-diff chunks. The
user message explicitly supplies the PR title, description, changed-file list,
and merged chunk results. Produce the whole-PR summaries now.

Return exactly one JSON object — no markdown fences, no prose — with this
shape:

```json
{
  "review_summary": {
    "summary": "Overall assessment of the whole PR in 1-3 sentences.",
    "notes": "Optional caveats."
  },
  "verification_summary": {
    "summary": "How the findings across all chunks were verified.",
    "approach": "chunked diff review"
  },
  "pr_summary": {
    "intent": "What the PR is trying to accomplish (decided in framing).",
    "work_type": "feature | change | bug | refactor | test-only | docs-config | mixed",
    "biggest_unknown": "The single largest context gap, or null.",
    "implementation_summary": "What the PR actually changes.",
    "architectural_impact": "Impact on the codebase structure, or empty.",
    "risk_assessment": "Main risks, or empty.",
    "positive_observations": []
  },
  "good_practices": [
    {"observation": "...", "evidence": "...", "files": []}
  ]
}
```

- Base every statement on the explicitly supplied PR framing evidence and merged
  chunk results; do not invent new findings.
- `pr_summary.intent` and `pr_summary.work_type` MUST be non-empty; determine
  them from the supplied PR title, description, and changed-file list.
- `review_summary.summary` and `verification_summary.summary` MUST be
  non-empty strings. Never emit placeholder text such as "Reviewed N chunks."
- `good_practices` is optional; omit it or return an empty list when nothing
  praiseworthy was observed.
- Return only the JSON object.
