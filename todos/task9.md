Task: Improve observability and structured logging

You are working in an existing automated PR reviewer repository.

Improve logging and run diagnostics.

Goals:
- Add structured stage-level logs.
- Log stage start/end, duration, status, and high-level counts.
- Include useful diagnostics such as:
  - PR ID
  - stage name
  - changed file count
  - chunk count
  - model name
  - finding counts before/after verification
  - posted comment count
  - vote result
- Add or update `run-summary.json`.
- Ensure logs are readable locally and useful in Azure Pipelines.
- Add tests for secret redaction and summary generation.

Security requirements:
- Never log tokens, API keys, auth headers, or full `.env`.
- Redact known secret keys.
- Do not write secrets to artifacts.

Constraints:
- Do not introduce excessive log noise.
- Preserve existing CLI behavior.
- Existing tests must pass.

Before editing, inspect current logging and artifact summary behavior.
Then implement incremental improvements.
