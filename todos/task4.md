Task: Split review generation from ADO posting

You are working in an existing automated PR reviewer repository.

Refactor the review flow so generating findings and posting findings to Azure DevOps can be run separately.

Desired commands:

python scripts/main.py review —pr 12345 —output artifacts/pr-12345/review.json
python scripts/main.py post —pr 12345 —input artifacts/pr-12345/review.json

The current all-in-one behavior must remain available.

Goals:
- `review` should be able to generate findings without posting.
- `post` should post a previously generated review result.
- Existing one-command review behavior should continue working, either by default or via an explicit option.
- Dry-run mode must never post comments or votes.
- Store generated output in a predictable artifact path.
- Add tests for review-only, post-only, dry-run, and existing combined flow.
- Update README.

Constraints:
- Do not duplicate business logic.
- Keep ADO posting idempotent.
- Do not change finding semantics unless necessary.
- Azure Pipelines must continue working.
- Existing tests must pass.

Before editing, inspect the current review/posting flow and produce a compatibility plan.
Then implement incrementally.
