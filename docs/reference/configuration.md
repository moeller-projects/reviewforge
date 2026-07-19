# Configuration

## Purpose

Document the `Config` dataclass, the env-var precedence rules, the alias map, and the `.env` loading contract. This is the **reference** for "what env var do I set to change X?" and the **how-to** for "I want to add a new tunable".

## Audience

- Operators wiring the bot into a CI pipeline or running it locally.
- Maintainers adding a new tunable.

## Precedence (highest to lowest)

```text
CLI flags  >  process env  >  .env file  >  built-in defaults
```

The CLI's argparse sets values on the namespace; `_build_config` only forwards the ones the user actually set (`getattr(args, field, None) not in (None, "")`). The merged env is built as `{**file_values, **os.environ}` inside `Config.from_env_file`, so process env wins over `.env` when both are set. CLI overrides are applied last via `Config.from_sources(cli, env=merged)`.

This means:

- Setting `OPENAI_API_KEY` in `.env` lets a fresh clone run without exports.
- Setting the same var on the shell overrides `.env`.
- Passing `--openai-api-key …` on the command line wins over both.

## How to set a value (how-to)

### In `.env` (persisted, shared with teammates)

```bash
# .env
ADO_AUTH_TOKEN=…
ADO_ORG=contoso
ADO_PROJECT=Pay
ADO_REPO_ID=api
PR_ID=1234
OPENAI_API_KEY=…
PI_MODEL=openai/gpt-5.5
REVIEW_LANGUAGE=English
```

`run.ps1` reads this file via `common.psm1`'s `Get-ReviewerEnvFile` and passes the path directly to `docker run --env-file` (Task 14). Inside the container, the Python `Config` reads the same vars through `os.environ`.

### On the shell (one-off override)

```bash
export ADO_AUTH_TOKEN=…
export OPENAI_API_KEY=…
./run.ps1 -PrUrl https://dev.azure.com/contoso/Pay/_git/api/pullrequest/1234
```

### As a CLI flag (highest priority)

```bash
./run.ps1 -PrId 1234 -Org contoso -Project Pay -RepoId api -AdoToken "..." -OpenAiApiKey "..." -DryRun
```

Inside the container, the equivalent direct CLI is:

```bash
python -m reviewforge review \
  --pr 1234 --org contoso --project Pay --repo api \
  --ado-token "..." --openai-api-key "..." --dry-run --fast-review
```

## Reference: every supported env var

The columns marked _Alias_ indicate that multiple env var names resolve to the same logical key. The first column (canonical) is what the rest of the package reads after resolution; the aliases are accepted as input.

### ADO identity

| Env var | Aliases | Default | Notes |
|---|---|---|---|
| `ADO_AUTH_TOKEN` | `SYSTEM_ACCESSTOKEN`, `ADO_MCP_AUTH_TOKEN`, `ADO_API_KEY` | _(required)_ | Bearer token. Azure Pipelines forwards `System.AccessToken`; local runs typically use a PAT. |
| `ADO_ORG` | — | _(required)_ | Short name (`contoso`) or full URL. |
| `ADO_PROJECT` | — | _(required)_ | Project name. |
| `ADO_REPO_ID` | — | _(required)_ | Repo id (GUID) or name. |
| `PR_ID` | `PR_URL` | _(required)_ | PR id, or a full PR URL (parsed by `parse_pr_url`). |
| `PR_URL` | — | — | Full PR URL. Mutually exclusive with `PR_ID`; URL wins if both are set. |
| `SOURCE_BRANCH` | — | _(auto-resolved)_ | Override the source branch. Otherwise the ADO API is queried. |
| `TARGET_BRANCH` | — | _(auto-resolved)_ | Override the target branch. |
| `REVIEW_TARGET_BRANCHES` | — | `""` | Comma-separated list. If set, PRs targeting branches not in this list are skipped (`should_skip` in the orchestrator). |

### Model + Pi

| Env var | Default | Notes |
|---|---|---|
| `OPENAI_API_KEY` | _(required by `pi`)_ | Provider API key. The Python package does not read this directly; it is forwarded to the `pi` subprocess env after ADO tokens are scrubbed. |
| `PI_MODEL` | `"openai/gpt-5.5"` | Model pattern. |
| `PI_TIMEOUT_SECS` | `600` | Per-stage `pi` subprocess timeout. |
| `PI_SESSION_ENABLED` | `"1"` | Set to `0` / `false` / `no` / `off` to disable session reuse (each stage gets a fresh `pi` call). |
| `PI_SESSION_ID` | _(auto)_ | Session id. Default: `pr-<pr_id>-review-<run_id>`. |
| `PI_SESSION_CLEAR` | `"0"` | Set to `1` to start a fresh session under the same id. |

### Review policy

| Env var | Default | Notes |
|---|---|---|
| `REVIEW_LANGUAGE` | `"English"` | Comment language. |
| `FAIL_ON` | `"none"` | `none` / `nit` / `minor` / `major` / `blocker`. The run fails (exit 2) when a finding at or above this threshold survives verification. |
| `VOTE_WAITING_ON` | `"none"` | Same set. The reviewer casts a "waiting for author" vote when findings meet the threshold. Default is `none` (no vote). |
| `POST_MIN_SEVERITY` | `"minor"` | Same set. Findings below this are dropped before posting. Set to `none` to disable. |
| `DROP_LOW_CONFIDENCE` | `"0"` | `1` to drop findings with `confidence == "low"`. |
| `REQUIRE_CONTEXT_FOR` | `""` | Comma-separated severities. Findings at these levels must have read context files (or a non-diff-only basis) or they are dropped. |
| `MAX_FINDINGS` | _(unset)_ | Cap on the number of findings, after filtering, sorted by severity. |
| `REVIEW_LANGUAGE` | `"English"` | Comment language hint. |
| `FORCE_REVIEW` | `"0"` | Set to `1` to review draft / closed / non-policy-branch PRs anyway. |
|| `DRY_RUN` | `"0"` | Set to `1` to skip posting; the final review JSON is printed to stdout. |
|| `REASONING_ENGINE` | `"single_pi"` | `single_pi` is the production one-call engine. Set `multi_stage` explicitly for debugging, benchmarking, regression comparison, or emergency fallback. |
|| `FAST_REVIEW` | `"0"` | Backwards-compatible alias that forces `REASONING_ENGINE=single_pi`. |

### Diff + context caps

| Env var | Default | Notes |
|---|---|---|
| `MAX_DIFF_BYTES` | `200000` | Maximum bytes retained by deterministic single-call diff reduction. |
| `CHUNK_TRIGGER_DIFF_BYTES` | `MAX_DIFF_BYTES` | Legacy multi-stage compatibility setting; it does not split production single-call reasoning. |
| `DISABLE_CHUNK_REVIEW` | `"0"` | Legacy compatibility setting. Production `single_pi` remains one logical review. |
| `CONTEXT_FILE_MAX_LINES` | `260` | Max lines of any file surfaced in `collected-context.json`. |
| `CONTEXT_SEARCH_MAX_MATCHES` | `40` | Max matches per `searches_to_run` query. |

### Artifacts + runtime

| Env var | Default | Notes |
|---|---|---|
| `REVIEW_ARTIFACT_ROOT` | `"/workspace/artifacts"` | Root for the per-PR directory tree. |
| `REVIEW_ARTIFACT_DIR` | _(unset)_ | If set, the runner uses this directory verbatim (no `pr-<id>/runs/<run_id>/` subdir). Useful for local debugging. |
| `REVIEW_ARTIFACT_VOLUME_NAME` | `"reviewforge-artifacts"` | Docker named volume for artifacts. |
| `REVIEW_RUN_ID` | _(auto)_ | Overrides the timestamp-based run id. Set for deterministic re-runs. |
| `WORKSPACE` | `"/workspace"` | Working dir inside the container. |
| `CLONE_ROOT` | `"/workspace/repo"` | Where the PR's source is cloned. |
| `IMAGE_NAME` | `"reviewforge:latest"` | Image tag. Alias: `IMAGE`. |
| `CONTAINER_NAME` | _(auto: `review-pr-<id>`)_ | Optional explicit container name. |
| `REVIEW_PROMPT_PATH` | _(required)_ | Path to the system prompt. |
| `INTENT_PROMPT_PATH` | _(required)_ | Path to the intent-stage prompt. |
| `CONTEXT_PLAN_PROMPT_PATH` | _(required)_ | Path to the plan-context-stage prompt. |
| `CONTEXT_DIGEST_PROMPT_PATH` | _(required)_ | Path to the context-digest-stage prompt. |
| `VERIFY_PROMPT_PATH` | _(required)_ | Path to the verify-stage prompt. |
| `SEVERITY_PROMPT_PATH` | _(required)_ | Path to the calibrate-severity-stage prompt. |
| `AC_COVERAGE_PROMPT_PATH` | `"/app/prompts/ac-coverage.md"` | Path to the optional AC-coverage LLM prompt. |
| `FAST_REVIEW_PROMPT_PATH` | `"/app/prompts/fast-review-system.md"` | Path to the fast-review system prompt. Required when `FAST_REVIEW=1`. |
| `STANDARDS_PATH` | _(required)_ | Path to the engineering-standards markdown. |

### AC coverage

| Env var | Default | Notes |
|---|---|---|
| `AC_COVERAGE_CHECK` | `"1"` | Set to `0` to skip the acceptance-criteria coverage stage entirely. |
| `AC_COVERAGE_DRY_RUN` | `"1"` | Set to `0` to skip AC coverage annotations in dry-run mode. |
| `AC_COVERAGE_LLM` | `"0"` | Set to `1` to enable an LLM second-pass that re-checks uncovered ACs and suppresses false positives. |
| `AC_COVERAGE_LLM_MAX_ACS` | `"10"` | Max uncovered ACs to send to the LLM per run. |

## Alias map (the canonical source)

`reviewforge.config._ENV_ALIASES` is a single dict that maps logical field names to a tuple of acceptable env-var names. The first name in the tuple wins when multiple are set.

```python
_ENV_ALIASES: dict[str, tuple[str, ...]] = {
    "ado_token":    ("SYSTEM_ACCESSTOKEN", "ADO_AUTH_TOKEN", "ADO_MCP_AUTH_TOKEN", "ADO_API_KEY"),
    "ado_org":      ("ADO_ORG",),
    "ado_project":  ("ADO_PROJECT",),
    "ado_repo_id":  ("ADO_REPO_ID",),
    "pr_id":        ("PR_ID", "PR_URL"),
    "source_branch":("SOURCE_BRANCH",),
    "target_branch":("TARGET_BRANCH",),
    "review_language": ("REVIEW_LANGUAGE",),
    "pi_model":     ("PI_MODEL",),
    "image":        ("IMAGE_NAME", "IMAGE"),
}
```

Resolution is done by `_read_env_with_aliases(key, env)`: it walks the alias tuple in order and returns the first non-empty value. `Config.from_sources` uses this for the token; the simple keys (`ado_org`, etc.) just use `os.getenv` directly because they have no aliases.

> Note on `ado_token`: `SYSTEM_ACCESSTOKEN` is checked first because Azure Pipelines always sets it (`System.AccessToken` is forwarded). Local PAT users set `ADO_AUTH_TOKEN`. The two `ADO_MCP_*` / `ADO_API_KEY` aliases exist for back-compat with the old MCP-based posting path.

## `.env` format

`parse_dotenv` follows the de-facto convention:

- Blank lines and `#` comments are skipped.
- Each non-empty line is `KEY=VALUE` (whitespace around the `=` is fine).
- Values may be wrapped in matching `"` or `'` quotes; quotes are stripped.
- No escape processing — keep it simple.
- Malformed lines are silently ignored.

The PowerShell side no longer ships an `Import-DotEnv` helper — the wrappers
read the live process environment only, on the principle that the user is
responsible for loading the file (via `direnv`, `set -a; source .env; set +a`,
etc.) and the file itself is data, not policy. The Python `parse_dotenv`
remains as a library helper for direct Python callers who want to load a file
explicitly; `Config.from_env_file(path)` uses it and merges the file values
*under* `os.environ` so the live env still wins.

## How to add a new tunable (how-to)

Three places need a coordinated edit when you add a new env var:

1. **`Config` dataclass in `src/reviewforge/config.py`** — add the field with a default and a comment. Make it a `field(default=…, compare=False)` if it should not participate in dataclass equality.
2. **Resolution in `Config.from_env` / `Config.from_sources`** — read the var, parse / validate, and pass it to the `cls(...)` constructor.
3. **CLI flag in `cli._build_common_parser`** — add `p.add_argument("--foo", dest="foo", …)` so users can override from the command line. Make sure `_apply_common` includes the new field in its tuple of override fields.

After the change:

4. **Tests in `tests/test_scripts_modules.py`** — add a `TestConfigFromEnv*` case that asserts the new field is populated from env and that CLI override wins.
5. **Test in `tests/test_cli.py::TestPowerShellForwardingContract`** — add a case that asserts the PowerShell wrapper forwards the new env var.
6. **README** — add a row to the env var table.

## Validation: when does a missing key fail?

`Config.from_env` raises `ConfigError` (a `ValueError` subclass) when a required key is missing. The CLI catches this in `_build_config` and prints a friendly message identifying the command and the missing key. The validator is `cfg.validate_for_command(command)`:

- `"review"` / `"post"` require org, project, repo, pr_id, token.
- `"validate-config"` runs the same checks but returns exit 1 instead of raising.
- `"discover"` only requires project + token (no PR-specific fields).

The CLI subcommand also calls `cfg.validate_files()` for review/post, which checks that all `*_prompt_path` and `standards_path` files exist on disk.
