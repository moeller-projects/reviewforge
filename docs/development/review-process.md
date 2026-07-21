# Review process

**Purpose:** define how code changes are checked. **Audience:** contributors and reviewers. **Mode:** how-to.

Review changes against the current implementation and caller graph. Confirm the canonical `ReviewResult` path, schema validation, secret handling, stage failure behavior, and artifact compatibility. For prompt changes, verify untrusted-input rules and exact output fields.

Before merge, run the changed-path tests, `pytest -q`, and a relevant CLI/config smoke check. Keep documentation claims grounded in source and parser output. Use OpenSpec artifacts when a behavior change has an active change record.
