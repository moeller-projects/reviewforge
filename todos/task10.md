Task: Formalize idempotent ADO posting

You are working in an existing automated PR reviewer repository.

Improve and formalize idempotency for Azure DevOps comments/votes.

Goals:
- Create a dedicated idempotency module or clearly isolated functions.
- Implement or preserve stable marker generation, e.g. `prb:{key}`.
- Add functions similar to:
  - `dedupe_key(finding) -> str`
  - `existing_bot_markers(threads) -> set[str]`
  - `should_post(finding, existing_markers) -> bool`
- Store posting results in `posted-comments.json`.
- Ensure rerunning the reviewer does not duplicate comments.
- Add tests for duplicate detection, changed comments, same-line findings, and reruns.

Constraints:
- Preserve existing marker format if already used.
- Do not delete or modify human comments.
- Dry-run mode must not post.
- Azure Pipelines must continue working.
- Existing tests must pass.

Before editing, inspect current ADO posting and marker logic.
Then produce an idempotency compatibility plan and implement incrementally.
