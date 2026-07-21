# Configuration reference

**Purpose:** document `Config` fields, defaults, and precedence. **Audience:** operators. **Mode:** reference.

Precedence is CLI override > process environment > `.env` file for most fields. `Config.from_env_file()` currently reads the three Pi session controls (`PI_SESSION_ID`, `PI_SESSION_ENABLED`, and `PI_SESSION_CLEAR`) directly from the process environment, so `.env`-only values for those fields are not applied.

| Field | Environment | Default |
|---|---|---|
| workspace | `WORKSPACE` | `/workspace` |
| clone_root | `CLONE_ROOT` | `/workspace/repo` |
| review_language | `REVIEW_LANGUAGE` | `English` |
| source_branch | `SOURCE_BRANCH` | empty; overrides PR metadata when set |
| target_branch | `TARGET_BRANCH` | empty; overrides PR metadata when set |
| pi_model | `PI_MODEL` | `openai/gpt-5.5` |
| max_diff_bytes | `MAX_DIFF_BYTES` | `200000` |
| chunk_trigger_diff_bytes | `CHUNK_TRIGGER_DIFF_BYTES` | `200000` |
| pi_timeout_secs | `PI_TIMEOUT_SECS` | `600` |
| include_work_items | `INCLUDE_WORK_ITEMS` | true |
| include_existing_comments | `INCLUDE_EXISTING_COMMENTS` | true |
| verify_findings | `VERIFY_FINDINGS` | true |
| reasoning_engine | `REASONING_ENGINE` | `single_pi` |
| review_artifact_root | `REVIEW_ARTIFACT_ROOT` | `/workspace/artifacts` |
| review_artifact_dir | `REVIEW_ARTIFACT_DIR` | unset |
| review_run_id | `REVIEW_RUN_ID` | unset |
| review_target_branches | `REVIEW_TARGET_BRANCHES` | empty |
| dry_run | `DRY_RUN` | false |
| force_review | `FORCE_REVIEW` | false |
| force_full_review | `FORCE_FULL_REVIEW` | false |
| pi_session_enabled | `PI_SESSION_ENABLED` | true |
| pi_session_clear | `PI_SESSION_CLEAR` | false |
| pi_session_id | `PI_SESSION_ID` | unset |

Posting fields are `POST_MIN_SEVERITY`, `DROP_LOW_CONFIDENCE`, `REQUIRE_CONTEXT_FOR`, `MAX_FINDINGS`, `VOTE_WAITING_ON`, and `FAIL_ON`. The primary `Config` default for `POST_MIN_SEVERITY` is `none`; the legacy `python -m reviewforge.ado.cli post-findings` helper defaults it to `minor` when unset. Other defaults are `DROP_LOW_CONFIDENCE=false`, `REQUIRE_CONTEXT_FOR` empty, `MAX_FINDINGS` unset, `VOTE_WAITING_ON=none`, and `FAIL_ON=none`. Context caps default to `CONTEXT_FILE_MAX_LINES=260`, `CONTEXT_SEARCH_MAX_MATCHES=40`, and `COLLECT_CONTEXT_WORKERS=8`.

AC coverage fields: `AC_COVERAGE_LLM=false`, `AC_COVERAGE_LLM_MAX_ACS=10`, and `AC_COVERAGE_PROMPT_PATH=/app/prompts/ac-coverage.md`. Prompt path variables are `REVIEW_PROMPT_PATH`, `INTENT_PROMPT_PATH`, `CONTEXT_PLAN_PROMPT_PATH`, `CONTEXT_DIGEST_PROMPT_PATH`, `VERIFY_PROMPT_PATH`, `SEVERITY_PROMPT_PATH`, `FAST_REVIEW_PROMPT_PATH`, and `REVIEW_STANDARDS_PATH`. `COMMENT_TEMPLATE_PATH` optionally selects a custom Jinja2 Markdown comment formatter; see [ADO integration](ado-integration.md).

Token aliases: `SYSTEM_ACCESSTOKEN`, `ADO_AUTH_TOKEN`, `ADO_MCP_AUTH_TOKEN`, `ADO_API_KEY`. PR identity aliases: `PR_ID`, `PR_URL`. `FAST_REVIEW=1` selects `single_pi` only when `REASONING_ENGINE` is unset; an explicit `REASONING_ENGINE` value wins.
