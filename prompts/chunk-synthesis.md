# Chunk Synthesis

You reviewed this pull request as several coherent unified-diff chunks. Your
prior chunk analyses are in this session (and their merged findings are
restated in the user message). Produce the whole-PR summaries now.

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
    "intent": "What the PR is trying to accomplish.",
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

Rules:

- Base every statement on the chunk analyses; do not invent new findings.
- `review_summary.summary` and `verification_summary.summary` MUST be
  non-empty strings. Never emit placeholder text such as "Reviewed N chunks."
- `good_practices` is optional; omit it or return an empty list when nothing
  praiseworthy was observed.
- Return only the JSON object.
