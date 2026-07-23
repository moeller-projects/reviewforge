## Context

Review state already normalizes ADO comments and determines follow-up mode. The posting marker fingerprint is rewording-tolerant but includes line numbers; feedback matching needs the same normalized file/title components without line numbers.

## Design

Add a small file/title fingerprint helper beside the existing posting key normalization. `review_state` extracts bot-authored thread titles from the existing formatted comment header, normalizes status deterministically, and serializes curated entries as `previousFeedback`. Reasoning instructions embed the curated list for both engines. After engine execution, the canonical result is revalidated after filtering matching dismissed findings without `regression: true`; removed findings become `DiscardedFinding` records. Regression findings proceed through the unchanged projection and posting path.

No sentiment analysis, dependency, prompt-side network access, or posting-marker change is introduced.
