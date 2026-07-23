# Environment variables

**Purpose:** provide the complete environment-name index. **Audience:** operators. **Mode:** reference.

ADO and PR: `SYSTEM_ACCESSTOKEN`, `ADO_AUTH_TOKEN`, `ADO_MCP_AUTH_TOKEN`, `ADO_API_KEY`, `ADO_ORG`, `ADO_PROJECT`, `ADO_REPO_ID`, `PR_ID`, `PR_URL`, `SOURCE_BRANCH`, `TARGET_BRANCH`. HTTP retries: `ADO_RETRY_ATTEMPTS` (default `3`), `ADO_RETRY_BASE_DELAY` (seconds, default `1`), `ADO_RETRY_CAP_DELAY` (seconds, default `30`), and `ADO_RETRY_BUDGET_SECS` (default `90`). Retries apply to safe GET transport/transient failures; POST/PUT retry only transport failures.

Review: `REVIEW_LANGUAGE`, `PI_MODEL`, `MAX_DIFF_BYTES`, `CHUNK_TRIGGER_DIFF_BYTES`, `DISABLE_CHUNK_REVIEW`, `PI_TIMEOUT_SECS`, `COMMIT_CONTEXT_MAX` (default `50`; commit subjects supplied as intent evidence), `ANCHOR_POLICY` (default `downgrade`; `drop` removes invalid diff anchors and `off` disables validation), `DRY_RUN`, `INCLUDE_WORK_ITEMS`, `INCLUDE_EXISTING_COMMENTS`, `VERIFY_FINDINGS`, `FORCE_REVIEW`, `FORCE_FULL_REVIEW`, `REVIEW_TARGET_BRANCHES`.

Paths and runs: `WORKSPACE`, `CLONE_ROOT`, `REVIEW_PROMPT_PATH`, `INTENT_PROMPT_PATH`, `CONTEXT_PLAN_PROMPT_PATH`, `CONTEXT_DIGEST_PROMPT_PATH`, `VERIFY_PROMPT_PATH`, `SEVERITY_PROMPT_PATH`, `REVIEW_STANDARDS_PATH`, `REVIEW_ARTIFACT_DIR`, `REVIEW_ARTIFACT_ROOT`, `REVIEW_RUN_ID`, `AC_COVERAGE_PROMPT_PATH`, `FAST_REVIEW_PROMPT_PATH`, `CHUNK_SYNTHESIS_PROMPT_PATH`, `COMMENT_TEMPLATE_PATH`.

Posting and context: `POST_MIN_SEVERITY`, `DROP_LOW_CONFIDENCE`, `REQUIRE_CONTEXT_FOR`, `MAX_FINDINGS`, `VOTE_WAITING_ON`, `FAIL_ON`, `CONTEXT_FILE_MAX_LINES`, `CONTEXT_SEARCH_MAX_MATCHES`, `COLLECT_CONTEXT_WORKERS`, `AC_COVERAGE_LLM`, `AC_COVERAGE_LLM_MAX_ACS`.

Engine and session: `REASONING_ENGINE`, `FAST_REVIEW`, `DEBUG_INTERMEDIATES` (default `0`; retain multi-stage fragment documents under `raw/` for debugging), `PI_SESSION_ID`, `PI_SESSION_ENABLED`, `PI_SESSION_CLEAR`, `REVIEW_LOG_LEVEL` (default `INFO`; standard-library log level for stderr and `run.log`).

Values are parsed by `Config` unless documented otherwise; boolean values accept `1`, `true`, `yes`, or `on` (case-insensitive).
