Task: Standardize review artifacts

You are working in an existing automated PR reviewer repository.

Make artifacts a first-class, documented contract.

Target artifact structure:

artifacts/pr-<PR_ID>/
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
  posted-comments.json
  run-summary.json

Goals:
- Use predictable per-PR artifact directories.
- Write artifacts consistently across local and CI runs.
- Add artifact path configuration.
- Avoid overwriting important data unexpectedly.
- Include useful metadata in `run-summary.json`.
- Document artifact meanings in README.
- Add tests for artifact paths and artifact writing.

Constraints:
- Preserve existing artifact outputs where possible.
- If old artifact paths exist, maintain compatibility or migrate safely.
- Do not log secrets into artifacts.
- Azure Pipelines artifact publishing must continue working.
- Existing tests must pass.

Before editing, inspect current artifact usage and produce a migration plan.
Then implement incrementally.
