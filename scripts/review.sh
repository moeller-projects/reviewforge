#!/usr/bin/env bash
set -Eeuo pipefail

log() { printf '[review] %s\n' "$*" >&2; }
die() { printf '[review][ERROR] %s\n' "$*" >&2; exit 1; }

on_err() {
local rc=$?
printf '[review][ERROR] line %s: %s (exit %s)\n' "${BASH_LINENO[0]}" "${BASH_COMMAND}" "$rc" >&2
exit "$rc"
}
trap on_err ERR

run_logged() {
local desc="$1"
shift
log "$desc"
"$@" 2>&1 | while IFS= read -r line; do
printf '[review][%s] %s\n' "$desc" "$line" >&2
done
}

require_uint() {
local name="$1" value="$2"
[[ "$value" =~ ^[0-9]+$ ]] || die "$name must be a non-negative integer, got: $value"
}

cleanup_paths=()
cleanup() {
local p
for p in "${cleanup_paths[@]:-}"; do
[ -n "$p" ] && rm -rf "$p"
done
}
trap cleanup EXIT

ADO_AUTH_TOKEN="${ADO_AUTH_TOKEN:-${ADO_MCP_AUTH_TOKEN:-}}"
: "${ADO_AUTH_TOKEN:?ADO_AUTH_TOKEN or ADO_MCP_AUTH_TOKEN is required}"
export ADO_AUTH_TOKEN
export ADO_MCP_AUTH_TOKEN="${ADO_MCP_AUTH_TOKEN:-$ADO_AUTH_TOKEN}"

urlencode() {
node -e 'process.stdout.write(encodeURIComponent(process.argv[1]))' "$1"
}

validate_review_output() {
local path="$1"
jq -e '
type == "object"
and (.summary | type == "string")
and (.findings | type == "array")
and all(.findings[]?;
type == "object"
and (.file == null or (.file | type == "string"))
and (.line == null or (.line | type == "number"))
and (.severity | IN("blocker", "major", "minor", "nit"))
and (.title | type == "string")
and ((has("context_basis") | not) or .context_basis == null or (.context_basis | IN("diff-only", "surrounding-code-read", "full-module-review")))
and ((has("evidence") | not) or .evidence == null or (
  .evidence | type == "object"
  and (.changed_lines | type == "array")
  and (.context_files_read | type == "array")
  and (.why_new_in_this_pr | type == "string")
  and (.why_not_intentional | type == "string")
))
and (.message | type == "string")
and ((has("confidence") | not) or .confidence == null or (.confidence | IN("high", "medium", "low")))
and ((has("suggestion") | not) or .suggestion == null or (.suggestion | type == "string"))
)
' "$path" >/dev/null || {
log "invalid Pi output:"
cat "$path" >&2
die "Pi output did not match expected JSON contract"
}
}

# --- Resolve PR identity: either PR_URL or individual vars -------------------
if [ -n "${PR_URL:-}" ]; then
  log "resolving PR_URL: $PR_URL"
  # Parse: https://dev.azure.com/{org}/{project}/_git/{repo}/pullrequest/{id}
  #   or: https://{org}.visualstudio.com/{project}/_git/{repo}/pullrequest/{id}
  PR_URL_PARSED=$(node -e '
    const u=process.argv[1];
    let m=u.match(/dev\.azure\.com\/([^/]+)\/([^/]+)\/_git\/([^/]+)\/pullrequest\/(\d+)/);
    if(!m) m=u.match(/:\/\/([^/]+)\.visualstudio\.com\/([^/]+)\/_git\/([^/]+)\/pullrequest\/(\d+)/);
    if(!m){process.stderr.write("Could not parse PR_URL\n");process.exit(1);}
    process.stdout.write(JSON.stringify({org:m[1],project:m[2],repo:m[3],prId:parseInt(m[4])}));
  ' "$PR_URL")
  ADO_ORG=$(printf '%s' "$PR_URL_PARSED" | jq -r '.org')
  ADO_PROJECT=$(printf '%s' "$PR_URL_PARSED" | jq -r '.project')
  ADO_REPO_ID=$(printf '%s' "$PR_URL_PARSED" | jq -r '.repo')
  PR_ID=$(printf '%s' "$PR_URL_PARSED" | jq -r '.prId')
  log "parsed: org=$ADO_ORG project=$ADO_PROJECT repo=$ADO_REPO_ID pr=$PR_ID"
fi

: "${ADO_ORG:?ADO_ORG is required (or set PR_URL)}"
: "${ADO_PROJECT:?ADO_PROJECT is required (or set PR_URL)}"
: "${ADO_REPO_ID:?ADO_REPO_ID is required (or set PR_URL)}"
: "${PR_ID:?PR_ID is required (or set PR_URL)}"

export ADO_ORG ADO_PROJECT ADO_REPO_ID PR_ID

ADO_AUTH_HEADER_PREFIX="Authorization:"
ADO_AUTH_SCHEME="Bearer"

SOURCE_BRANCH="${SOURCE_BRANCH:-${SYSTEM_PULLREQUEST_SOURCEBRANCH:-}}"
TARGET_BRANCH="${TARGET_BRANCH:-${SYSTEM_PULLREQUEST_TARGETBRANCH:-}}"

# Auto-resolve branches from ADO REST API when not provided
if [ -z "$SOURCE_BRANCH" ] || [ -z "$TARGET_BRANCH" ]; then
  ADO_API_BASE_EARLY="https://dev.azure.com/$(urlencode "$ADO_ORG")/$(urlencode "$ADO_PROJECT")"
  log "auto-resolving branches from ADO REST API"
  PR_API_URL="${ADO_API_BASE_EARLY}/_apis/git/repositories/$(urlencode "$ADO_REPO_ID")/pullRequests/${PR_ID}"
  log "  GET $PR_API_URL"
  PR_API_DATA=$(curl -sS \
    -H "${ADO_AUTH_HEADER_PREFIX} ${ADO_AUTH_SCHEME} ${ADO_AUTH_TOKEN}" \
    -H "Accept: application/json;api-version=7.1" \
    "$PR_API_URL" 2>&1) || true
  # Check for HTTP-level errors in the response
  if printf '%s' "$PR_API_DATA" | jq -e '.message // empty' >/dev/null 2>&1; then
    API_ERR=$(printf '%s' "$PR_API_DATA" | jq -r '.message // empty')
    [ -z "$API_ERR" ] || log "  ADO API error: $API_ERR"
  fi
  log "  API response (truncated): $(printf '%s' "$PR_API_DATA" | head -c 300)"
  if [ -z "$SOURCE_BRANCH" ]; then
    SOURCE_BRANCH=$(printf '%s' "$PR_API_DATA" | jq -r '.sourceRefName // empty' 2>/dev/null || true)
    [ -n "$SOURCE_BRANCH" ] || die "could not resolve source branch from API (response logged above)"
    log "  source branch: $SOURCE_BRANCH (from API)"
  fi
  if [ -z "$TARGET_BRANCH" ]; then
    TARGET_BRANCH=$(printf '%s' "$PR_API_DATA" | jq -r '.targetRefName // empty' 2>/dev/null || true)
    [ -n "$TARGET_BRANCH" ] || die "could not resolve target branch from API (response logged above)"
    log "  target branch: $TARGET_BRANCH (from API)"
  fi
fi

SOURCE_BRANCH="${SOURCE_BRANCH#refs/heads/}"
TARGET_BRANCH="${TARGET_BRANCH#refs/heads/}"

WORKSPACE="${WORKSPACE:-/workspace}"
CLONE_ROOT="${CLONE_ROOT:-$WORKSPACE}"
REVIEW_LANGUAGE="${REVIEW_LANGUAGE:-English}"
REVIEW_PROMPT_PATH="${REVIEW_PROMPT_PATH:-/app/prompts/review-system.md}"
REVIEW_INTENT_PROMPT_PATH="${REVIEW_INTENT_PROMPT_PATH:-/app/prompts/intent.md}"
REVIEW_CONTEXT_PLAN_PROMPT_PATH="${REVIEW_CONTEXT_PLAN_PROMPT_PATH:-/app/prompts/context-plan.md}"
REVIEW_CONTEXT_DIGEST_PROMPT_PATH="${REVIEW_CONTEXT_DIGEST_PROMPT_PATH:-/app/prompts/context-digest.md}"
REVIEW_VERIFY_PROMPT_PATH="${REVIEW_VERIFY_PROMPT_PATH:-/app/prompts/verify-findings.md}"
REVIEW_SEVERITY_PROMPT_PATH="${REVIEW_SEVERITY_PROMPT_PATH:-/app/prompts/severity.md}"
REVIEW_STANDARDS_PATH="${REVIEW_STANDARDS_PATH:-/app/standards/clean-code.md}"
PI_MODEL="${PI_MODEL:-openai/gpt-5.5}"
MAX_DIFF_BYTES="${MAX_DIFF_BYTES:-200000}"
CHUNK_TRIGGER_DIFF_BYTES="${CHUNK_TRIGGER_DIFF_BYTES:-$MAX_DIFF_BYTES}"
DISABLE_CHUNK_REVIEW="${DISABLE_CHUNK_REVIEW:-0}"
PI_TIMEOUT_SECS="${PI_TIMEOUT_SECS:-600}"
DRY_RUN="${DRY_RUN:-0}"
INCLUDE_WORK_ITEMS="${INCLUDE_WORK_ITEMS:-1}"
INCLUDE_EXISTING_COMMENTS="${INCLUDE_EXISTING_COMMENTS:-1}"
VOTE_WAITING_ON="${VOTE_WAITING_ON:-major}"
VERIFY_FINDINGS="${VERIFY_FINDINGS:-1}"
FORCE_REVIEW="${FORCE_REVIEW:-0}"
# Comma-separated list of target branch names that are eligible for review.
# Empty means all branches are reviewed.
REVIEW_TARGET_BRANCHES="${REVIEW_TARGET_BRANCHES:-}"

require_uint MAX_DIFF_BYTES "$MAX_DIFF_BYTES"
require_uint CHUNK_TRIGGER_DIFF_BYTES "$CHUNK_TRIGGER_DIFF_BYTES"
require_uint PI_TIMEOUT_SECS "$PI_TIMEOUT_SECS"

is_true() {
  case "${1,,}" in
    1|true|yes|on) return 0 ;;
    *) return 1 ;;
  esac
}

[ -r "$REVIEW_PROMPT_PATH" ] || die "review prompt not readable: $REVIEW_PROMPT_PATH"
[ -r "$REVIEW_INTENT_PROMPT_PATH" ] || die "intent prompt not readable: $REVIEW_INTENT_PROMPT_PATH"
[ -r "$REVIEW_CONTEXT_PLAN_PROMPT_PATH" ] || die "context plan prompt not readable: $REVIEW_CONTEXT_PLAN_PROMPT_PATH"
[ -r "$REVIEW_CONTEXT_DIGEST_PROMPT_PATH" ] || die "context digest prompt not readable: $REVIEW_CONTEXT_DIGEST_PROMPT_PATH"
[ -r "$REVIEW_VERIFY_PROMPT_PATH" ] || die "verification prompt not readable: $REVIEW_VERIFY_PROMPT_PATH"
[ -r "$REVIEW_SEVERITY_PROMPT_PATH" ] || die "severity prompt not readable: $REVIEW_SEVERITY_PROMPT_PATH"
[ -r "$REVIEW_STANDARDS_PATH" ] || die "review standards not readable: $REVIEW_STANDARDS_PATH"

command -v git >/dev/null || die "git is required"
command -v node >/dev/null || die "node is required"
command -v pi >/dev/null || die "pi is required"
command -v jq >/dev/null || die "jq is required"
command -v curl >/dev/null || die "curl is required"

export PI_SKIP_VERSION_CHECK=1
export PI_TELEMETRY=0
export PI_OFFLINE=0

mkdir -p "$CLONE_ROOT"

REPO_DIR="$(mktemp -d "${CLONE_ROOT%/}/repo.XXXXXX")"
AUTH_DIR="$(mktemp -d)"
DIFF_FILE="$(mktemp)"
FILES_FILE="$(mktemp)"
RAW_OUT="$(mktemp)"
SYS_FILE="$(mktemp)"
WI_FILE="$(mktemp)"
THREADS_FILE="$(mktemp)"
WI_COMMENTS_FILE="$(mktemp)"
CHUNK_DIR="$(mktemp -d)"
ARTIFACT_DIR="${REVIEW_ARTIFACT_DIR:-$WORKSPACE/artifacts/pr-$PR_ID}"
METADATA_FILE="$ARTIFACT_DIR/metadata.json"
INTENT_FILE="$ARTIFACT_DIR/intent.json"
CONTEXT_PLAN_FILE="$ARTIFACT_DIR/context-plan.json"
CONTEXT_DIGEST_FILE="$ARTIFACT_DIR/context-digest.json"
CANDIDATE_FINDINGS_FILE="$ARTIFACT_DIR/candidate-findings.json"
VERIFIED_FINDINGS_FILE="$ARTIFACT_DIR/verified-findings.json"
SEVERITY_FINDINGS_FILE="$ARTIFACT_DIR/severity-findings.json"
FINAL_FINDINGS_FILE="$ARTIFACT_DIR/final-findings.json"
COLLECTED_CONTEXT_FILE="$ARTIFACT_DIR/collected-context.json"
mkdir -p "$ARTIFACT_DIR"
cleanup_paths+=("$REPO_DIR" "$AUTH_DIR" "$DIFF_FILE" "$FILES_FILE" "$RAW_OUT" "$SYS_FILE" "$WI_FILE" "$THREADS_FILE" "$WI_COMMENTS_FILE" "$CHUNK_DIR" "${DIFF_FILE}.cut")

ADO_API_BASE="https://dev.azure.com/$(urlencode "$ADO_ORG")/$(urlencode "$ADO_PROJECT")"
REPO_URL="${ADO_API_BASE}/_git/$(urlencode "$ADO_REPO_ID")"

ado_get() {
local path="$1"
curl -sS -H "${ADO_AUTH_HEADER_PREFIX} ${ADO_AUTH_SCHEME} ${ADO_AUTH_TOKEN}" \
  -H "Accept: application/json;api-version=7.1" \
  "${ADO_API_BASE}${path}" 2>/dev/null || echo '{}'
}

ado_post_json() {
local path="$1" body="$2"
curl -sS -H "${ADO_AUTH_HEADER_PREFIX} ${ADO_AUTH_SCHEME} ${ADO_AUTH_TOKEN}" \
  -H "Accept: application/json;api-version=7.1" \
  -H "Content-Type: application/json" \
  -d "$body" \
  "${ADO_API_BASE}${path}" 2>/dev/null || echo '{}'
}

build_system_prompt() {
cat "$REVIEW_PROMPT_PATH" > "$SYS_FILE"
{
printf '\n\n---\n'
printf 'LANGUAGE: Write every "title", "message", "summary", and "suggestion" value in %s. Do NOT translate file paths, identifiers, or code.\n' "$REVIEW_LANGUAGE"
printf '%s\n\n' '---'
cat "$REVIEW_STANDARDS_PATH"
} >> "$SYS_FILE"
}

write_instruction() {
local files_path="$1"
local out_path="$2"
local chunk_label="${3:-}"
local truncated_flag="${4:-0}"

{
printf 'Review the unified diff provided on stdin.\n'
printf 'The PR range is merge-base(target, source)..source.\n'
printf 'Target branch: %s\n' "$TARGET_BRANCH"
printf 'Source branch: %s\n' "$SOURCE_BRANCH"
printf 'Target commit: %s\n' "$TARGET_COMMIT"
printf 'Source commit: %s\n' "$SOURCE_COMMIT"
printf 'Merge-base: %s\n\n' "$BASE_COMMIT"
printf 'Changed files:\n'
cat "$files_path"
printf '\n'

if [ -n "$chunk_label" ]; then
printf '%s\n\n' '---'
printf 'LARGE DIFF CHUNK\n'
printf 'This review covers %s of a large PR split by file to preserve context. Review ONLY the files listed in this chunk. Do NOT infer missing implementation, missing work-item coverage, or other findings from files that are not present in this chunk.\n\n' "$chunk_label"
fi

if [ -n "$WI_CONTEXT" ] && [ "$WI_CONTEXT" != "[]" ]; then
printf '%s\n\n' '---'
printf 'LINKED WORK ITEMS\n'
printf "The following work items are linked to this PR. Verify that the changes fulfill each work item's description and acceptance criteria. If a requirement is not addressed by the diff, create a finding with severity at least \"major\", file=null, line=null.\n\n"
printf '%s\n' "$(printf '%s' "$WI_CONTEXT" | jq -r '.[] | "Work Item #\(.id) [\(.type)] \(.title) (State: \(.state))\n  Description: \(.description)\n  Acceptance Criteria: \(.acceptanceCriteria)"')"
printf '\n'

if [ -n "${WI_COMMENTS_CONTEXT:-}" ] && [ "${WI_COMMENTS_CONTEXT:-}" != "[]" ]; then
  printf 'WORK ITEM COMMENTS (respect these as additional context for requirements)\n'
  printf '%s\n' "$(printf '%s' "$WI_COMMENTS_CONTEXT" | jq -r '.[] | "Work Item #\(.workItemId) comments:" + (.comments | map("  [\(.author)] \(.text[0:500])") | join("\n"))')"
  printf '\n'
fi
fi

if [ -n "$THREAD_CONTEXT" ] && [ "$THREAD_CONTEXT" != "[]" ]; then
printf '%s\n\n' '---'
printf 'EXISTING PR COMMENTS\n'
printf 'The following comments already exist on this PR. Do NOT create a finding that covers the same issue already raised in these comments. If an existing comment discusses an issue, consider it already addressed by the review process.\n\n'
printf '%s\n' "$(printf '%s' "$THREAD_CONTEXT" | jq -r '.[] | "[\(.author)] \(if .filePath then "\(.filePath):\(.line)" else "(general)" end): \(.firstComment[0:300])"')"
printf '\n'
fi

if [ -s "$INTENT_FILE" ]; then
printf '%s\n\n' '---'
printf 'PR INTENT RECONSTRUCTION\n'
cat "$INTENT_FILE"
printf '\n'
fi

if [ -s "$CONTEXT_DIGEST_FILE" ]; then
printf '%s\n\n' '---'
printf 'CONTEXT DIGEST\n'
cat "$CONTEXT_DIGEST_FILE"
printf '\nUse this digest as evidence. If a candidate issue is plausibly intentional according to this context, do not report it.\n'
fi

if [ "$truncated_flag" = "1" ]; then
printf 'NOTE: The diff was truncated due to size. Review only what is present and mention truncation in the summary.\n'
fi
printf 'Return ONLY the JSON object defined in your instructions.\n'
} > "$out_path"
}

validate_json_output() {
local path="$1" label="$2"
jq -e 'type == "object"' "$path" >/dev/null || die "$label output is not a JSON object"
case "$label" in
  "intent reconstruction")
    jq -e '(.pr_intent | type == "string") and (.changed_behaviors | type == "array") and (.risk_areas | type == "array")' "$path" >/dev/null || die "$label output does not match intent schema"
    ;;
  "context planning")
    jq -e '(.files_to_read | type == "array") and (.searches_to_run | type == "array") and (.tests_to_inspect | type == "array")' "$path" >/dev/null || die "$label output does not match context-plan schema"
    ;;
  "context digest")
    jq -e '(.relevant_context | type == "array") and (.possible_intentional_choices | type == "array") and (.context_gaps | type == "array")' "$path" >/dev/null || die "$label output does not match context-digest schema"
    ;;
  "finding verification"|"severity calibration"|"candidate findings"*)
    validate_review_output "$path"
    ;;
esac
}

strip_json_fences() {
# Remove markdown code fences that some models wrap around JSON output.
sed '/^```/d' "$1" > "$1.stripped"
mv "$1.stripped" "$1"
}

write_stage_instruction() {
local stage="$1"
local out_path="$2"
local diff_path="${3:-$DIFF_FILE}"
{
printf '%s stage for Azure DevOps PR #%s. Return only the JSON object requested by the system prompt.\n\n' "$stage" "$PR_ID"
printf 'Repository/project metadata:\n'
cat "$METADATA_FILE"
printf '\n\nChanged files:\n'
cat "$FILES_FILE"
printf '\n\nLinked work items:\n%s\n' "${WI_CONTEXT:-[]}"
printf '\nExisting PR comments:\n%s\n' "${THREAD_CONTEXT:-[]}"
if [ -s "$INTENT_FILE" ]; then printf '\nIntent reconstruction:\n'; cat "$INTENT_FILE"; printf '\n'; fi
if [ -s "$CONTEXT_PLAN_FILE" ]; then printf '\nContext collection plan:\n'; cat "$CONTEXT_PLAN_FILE"; printf '\n'; fi
if [ -s "$COLLECTED_CONTEXT_FILE" ]; then printf '\nRunner-collected context:\n'; cat "$COLLECTED_CONTEXT_FILE"; printf '\n'; fi
if [ -s "$CONTEXT_DIGEST_FILE" ]; then printf '\nContext digest:\n'; cat "$CONTEXT_DIGEST_FILE"; printf '\n'; fi
if [ -s "$CANDIDATE_FINDINGS_FILE" ]; then printf '\nCandidate findings:\n'; cat "$CANDIDATE_FINDINGS_FILE"; printf '\n'; fi
if [ -s "$VERIFIED_FINDINGS_FILE" ]; then printf '\nVerified findings:\n'; cat "$VERIFIED_FINDINGS_FILE"; printf '\n'; fi
printf '\nUnified diff follows on stdin.\n'
} > "$out_path"
}

run_pi_stage() {
local stage="$1"
local prompt_path="$2"
local diff_path="$3"
local out_path="$4"
local instruction_path stdin_path
instruction_path="$(mktemp)"
stdin_path="$(mktemp)"
cleanup_paths+=("$instruction_path" "$stdin_path")
write_stage_instruction "$stage" "$instruction_path" "$diff_path"
# Combine instruction + diff into a single stdin file to avoid ARG_MAX limits
cat "$instruction_path" "$diff_path" > "$stdin_path"

log "running Pi $stage stage (timeout: ${PI_TIMEOUT_SECS}s)"
set +e
timeout "$PI_TIMEOUT_SECS" env -u ADO_AUTH_TOKEN -u ADO_MCP_AUTH_TOKEN pi \
  --no-session --no-context-files --no-extensions --no-skills --no-prompt-templates \
  --tools read,grep \
  --model "$PI_MODEL" --thinking medium \
  --append-system-prompt "$prompt_path" \
  -p "Process the task described in the system prompt. The instruction and unified diff are provided on stdin." \
  < "$stdin_path" > "$out_path"
PI_RC=$?
set -e

if [ "$PI_RC" -eq 124 ]; then
  die "Pi $stage stage timed out after ${PI_TIMEOUT_SECS}s. Increase PI_TIMEOUT_SECS to allow more time."
fi
log "pi $stage exit code: $PI_RC"
[ "$PI_RC" -eq 0 ] || die "pi $stage stage exited $PI_RC"
[ -s "$out_path" ] || die "pi $stage stage produced no output"

# Attempt to parse JSON; if invalid, strip fences and retry once with repair prompt.
if ! jq -e '.' "$out_path" >/dev/null 2>&1; then
  log "pi $stage output is not valid JSON – trying fence-strip"
  strip_json_fences "$out_path"
  if ! jq -e '.' "$out_path" >/dev/null 2>&1; then
    log "fence-strip did not fix JSON – sending repair prompt to Pi"
    local repair_out
    repair_out="$(mktemp)"
    cleanup_paths+=("$repair_out")
    set +e
    timeout "$PI_TIMEOUT_SECS" env -u ADO_AUTH_TOKEN -u ADO_MCP_AUTH_TOKEN pi \
      --no-session --no-context-files --no-extensions --no-skills --no-prompt-templates \
      --tools read,grep \
      --model "$PI_MODEL" --thinking medium \
      --append-system-prompt "$prompt_path" \
      -p "Your previous response was not valid JSON. Return only the JSON object – no markdown fences, no prose." \
      < "$stdin_path" > "$repair_out"
    REPAIR_RC=$?
    set -e
    if [ "$REPAIR_RC" -ne 0 ] || [ ! -s "$repair_out" ]; then
      die "Pi $stage repair call failed (rc=$REPAIR_RC)"
    fi
    strip_json_fences "$repair_out"
    jq -e '.' "$repair_out" >/dev/null 2>&1 || die "Pi $stage repair response is still invalid JSON"
    cp "$repair_out" "$out_path"
  fi
fi

validate_json_output "$out_path" "$stage"
}

collect_context_from_plan() {
log "collecting deterministic context from context plan"
local max_file_lines="${CONTEXT_FILE_MAX_LINES:-260}"
local max_search_matches="${CONTEXT_SEARCH_MAX_MATCHES:-40}"
local tmp_context
local tmp_files
local tmp_searches
local tmp_tests

tmp_context="$(mktemp)"
tmp_files="$(mktemp)"
tmp_searches="$(mktemp)"
tmp_tests="$(mktemp)"
cleanup_paths+=("$tmp_context" "$tmp_files" "$tmp_searches" "$tmp_tests")

jq -r '.files_to_read[]? | [.path, (.reason // "")] | @tsv' "$CONTEXT_PLAN_FILE" > "$tmp_files"
jq -r '.searches_to_run[]? | [.query, (.reason // "")] | @tsv' "$CONTEXT_PLAN_FILE" > "$tmp_searches"
jq -r '.tests_to_inspect[]? | tostring' "$CONTEXT_PLAN_FILE" > "$tmp_tests"

{
printf '{"files":['
local first=1
while IFS=$'\t' read -r path reason; do
  [ -n "$path" ] || continue
  case "$path" in /*|*..*) continue ;; esac
  if [ -f "$path" ]; then
    if [ "$first" -eq 0 ]; then printf ','; fi
    first=0
    jq -n \
      --arg path "$path" \
      --arg reason "$reason" \
      --arg content "$(sed -n "1,${max_file_lines}p" "$path")" \
      --arg truncated "$(if [ "$(wc -l < "$path" | tr -d '[:space:]')" -gt "$max_file_lines" ]; then printf true; else printf false; fi)" \
      '{path:$path, reason:$reason, truncated:($truncated == "true"), content:$content}'
  fi
done < "$tmp_files"
printf '],"tests":['
first=1
while IFS= read -r hint; do
  [ -n "$hint" ] || continue
  case "$hint" in /*|*..*) continue ;; esac
  if [ -f "$hint" ]; then
    if [ "$first" -eq 0 ]; then printf ','; fi
    first=0
    jq -n --arg path "$hint" --arg content "$(sed -n "1,${max_file_lines}p" "$hint")" '{path:$path, content:$content}'
  fi
done < "$tmp_tests"
printf '],"searches":['
first=1
while IFS=$'\t' read -r query reason; do
  [ -n "$query" ] || continue
  if [ "$first" -eq 0 ]; then printf ','; fi
  first=0
  local matches_file
  matches_file="$(mktemp)"
  cleanup_paths+=("$matches_file")
  rg -n --fixed-strings --glob '!/.git/**' --glob '!node_modules/**' --glob '!artifacts/**' -- "$query" . 2>/dev/null | head -n "$max_search_matches" > "$matches_file" || true
  jq -n --arg query "$query" --arg reason "$reason" --rawfile matches "$matches_file" '{query:$query, reason:$reason, matches:$matches}'
done < "$tmp_searches"
printf ']}'
} > "$tmp_context"

jq '.' "$tmp_context" > "$COLLECTED_CONTEXT_FILE"
}

run_pi_review() {
local diff_path="$1"
local files_path="$2"
local out_path="$3"
local chunk_label="${4:-}"
local truncated_flag="${5:-0}"
local instruction_path stdin_path
instruction_path="$(mktemp)"
stdin_path="$(mktemp)"
cleanup_paths+=("$instruction_path" "$stdin_path")

write_instruction "$files_path" "$instruction_path" "$chunk_label" "$truncated_flag"
# Combine instruction + diff into a single stdin file to avoid ARG_MAX limits
cat "$instruction_path" "$diff_path" > "$stdin_path"

log "running Pi reviewer (timeout: ${PI_TIMEOUT_SECS}s)"
set +e
timeout "$PI_TIMEOUT_SECS" env -u ADO_AUTH_TOKEN -u ADO_MCP_AUTH_TOKEN pi \
  --no-session --no-context-files --no-extensions --no-skills --no-prompt-templates \
  --tools read,grep \
  --model "$PI_MODEL" --thinking medium \
  --append-system-prompt "$SYS_FILE" \
  -p "Process the task described in the system prompt. The instruction and unified diff are provided on stdin." \
  < "$stdin_path" > "$out_path"
PI_RC=$?
set -e

if [ "$PI_RC" -eq 124 ]; then
  die "Pi reviewer timed out after ${PI_TIMEOUT_SECS}s. Increase PI_TIMEOUT_SECS to allow more time."
fi
log "pi exit code: $PI_RC"
[ "$PI_RC" -eq 0 ] || die "pi exited $PI_RC"
[ -s "$out_path" ] || die "pi produced no output"

# Attempt to parse JSON; if invalid, strip fences and retry once with repair prompt.
if ! jq -e '.' "$out_path" >/dev/null 2>&1; then
  log "pi review output is not valid JSON – trying fence-strip"
  strip_json_fences "$out_path"
  if ! jq -e '.' "$out_path" >/dev/null 2>&1; then
    log "fence-strip did not fix JSON – sending repair prompt to Pi"
    local repair_out
    repair_out="$(mktemp)"
    cleanup_paths+=("$repair_out")
    set +e
    timeout "$PI_TIMEOUT_SECS" env -u ADO_AUTH_TOKEN -u ADO_MCP_AUTH_TOKEN pi \
      --no-session --no-context-files --no-extensions --no-skills --no-prompt-templates \
      --tools read,grep \
      --model "$PI_MODEL" --thinking medium \
      --append-system-prompt "$SYS_FILE" \
      -p "Your previous response was not valid JSON. Return only the JSON object – no markdown fences, no prose." \
      < "$stdin_path" > "$repair_out"
    REPAIR_RC=$?
    set -e
    if [ "$REPAIR_RC" -ne 0 ] || [ ! -s "$repair_out" ]; then
      die "Pi review repair call failed (rc=$REPAIR_RC)"
    fi
    strip_json_fences "$repair_out"
    jq -e '.' "$repair_out" >/dev/null 2>&1 || die "Pi review repair response is still invalid JSON"
    cp "$repair_out" "$out_path"
  fi
fi

validate_review_output "$out_path"
}

GIT_ASKPASS="$AUTH_DIR/git-askpass.sh"
cat > "$GIT_ASKPASS" <<'EOF'
#!/usr/bin/env bash
case "$1" in
*Username*) printf '%s\n' "x-access-token" ;;
*) printf '%s\n' "${ADO_AUTH_TOKEN}" ;;
esac
EOF
chmod 700 "$GIT_ASKPASS"
export GIT_ASKPASS
export GIT_TERMINAL_PROMPT=0

log "initializing reviewed repo in $REPO_DIR"
cd "$REPO_DIR"

run_logged "git init" git init
run_logged "git remote add origin" git remote add origin "$REPO_URL"
run_logged "git config safe.directory" git config --global --add safe.directory "$REPO_DIR" || true

TARGET_REF="refs/pr-review/target"
SOURCE_REF="refs/pr-review/source"

run_logged "git fetch target" git fetch --no-tags --depth=200 origin \
  "+refs/heads/${TARGET_BRANCH}:${TARGET_REF}" \
  || die "failed to fetch target branch: $TARGET_BRANCH"

run_logged "git fetch source" git fetch --no-tags --depth=200 origin \
  "+refs/heads/${SOURCE_BRANCH}:${SOURCE_REF}" \
  || die "failed to fetch source branch: $SOURCE_BRANCH"

if ! git merge-base "$TARGET_REF" "$SOURCE_REF" >/dev/null 2>&1; then
  log "merge-base not found in shallow fetch; deepening history"
  run_logged "git fetch deepen target" git fetch --no-tags --deepen=1000 origin \
    "+refs/heads/${TARGET_BRANCH}:${TARGET_REF}" \
    || die "failed to deepen target branch: $TARGET_BRANCH"
  run_logged "git fetch deepen source" git fetch --no-tags --deepen=1000 origin \
    "+refs/heads/${SOURCE_BRANCH}:${SOURCE_REF}" \
    || die "failed to deepen source branch: $SOURCE_BRANCH"
fi

git merge-base "$TARGET_REF" "$SOURCE_REF" >/dev/null \
  || die "could not determine merge-base for $TARGET_BRANCH...$SOURCE_BRANCH"

TARGET_COMMIT="$(git rev-parse --verify "${TARGET_REF}^{commit}")"
SOURCE_COMMIT="$(git rev-parse --verify "${SOURCE_REF}^{commit}")"
BASE_COMMIT="$(git merge-base "$TARGET_REF" "$SOURCE_REF")"

log "target $TARGET_BRANCH -> $TARGET_COMMIT"
log "source $SOURCE_BRANCH -> $SOURCE_COMMIT"
log "merge-base -> $BASE_COMMIT"

run_logged "git checkout source" git checkout "$SOURCE_COMMIT"

RANGE="${BASE_COMMIT}..${SOURCE_COMMIT}"

git diff --unified=3 --no-ext-diff "$RANGE" > "$DIFF_FILE"
git diff --name-only --no-ext-diff "$RANGE" > "$FILES_FILE"

DIFF_BYTES="$(wc -c < "$DIFF_FILE" | tr -d '[:space:]')"
FILE_COUNT="$(wc -l < "$FILES_FILE" | tr -d '[:space:]')"

log "changed files: $FILE_COUNT"
log "diff size: ${DIFF_BYTES} bytes"

TRUNCATED=0

log "fetching Azure DevOps PR context via Python helper"
python3 /app/scripts/ado_review.py fetch-context \
  --org "$ADO_ORG" \
  --project "$ADO_PROJECT" \
  --repo "$ADO_REPO_ID" \
  --pr "$PR_ID" \
  --out "$ARTIFACT_DIR"

jq \
  --arg baseCommit "$BASE_COMMIT" \
  --arg sourceCommit "$SOURCE_COMMIT" \
  --arg targetCommit "$TARGET_COMMIT" \
  --rawfile files "$FILES_FILE" \
  '. + {
    baseCommit: $baseCommit,
    sourceCommit: $sourceCommit,
    targetCommit: $targetCommit,
    changedFiles: ($files | split("\n") | map(select(length > 0)))
  }' "$METADATA_FILE" > "${METADATA_FILE}.tmp"
mv "${METADATA_FILE}.tmp" "$METADATA_FILE"
cp "$DIFF_FILE" "$ARTIFACT_DIR/diff.patch"

# Generate commits.txt artifact
git log --oneline "$RANGE" > "$ARTIFACT_DIR/commits.txt"

# Generate changed-files.json artifact (replaces plain changed-files.txt)
build_changed_files_json() {
  local files_path="$1"
  local out_path="$2"
  node -e '
    const fs = require("node:fs");
    const lines = fs.readFileSync(process.argv[1], "utf8").split("\n").filter(Boolean);
    const EXT_LANG = {
      ts:"TypeScript", tsx:"TypeScript", js:"JavaScript", jsx:"JavaScript",
      mjs:"JavaScript", cjs:"JavaScript", py:"Python", rb:"Ruby",
      go:"Go", java:"Java", cs:"C#", cpp:"C++", cc:"C++", c:"C", h:"C",
      rs:"Rust", kt:"Kotlin", swift:"Swift", php:"PHP", sh:"Shell",
      bash:"Shell", ps1:"PowerShell", psm1:"PowerShell", psd1:"PowerShell",
      tf:"HCL", hcl:"HCL", json:"JSON", yaml:"YAML", yml:"YAML",
      md:"Markdown", html:"HTML", css:"CSS", scss:"SCSS", sql:"SQL",
    };
    const TEST_FILE_RE = /(\.(test|spec)\.[^.]+$|_test\.[^.]+$|Test\.[^.]+$)/;
    const TEST_PATH_RE = /(^|\/)(test|tests|__tests__|spec|specs)\//;
    const entries = lines.map(f => {
      const ext = f.split(".").pop().toLowerCase();
      return {
        file: f,
        language: EXT_LANG[ext] ?? "Other",
        isTest: TEST_FILE_RE.test(f) || TEST_PATH_RE.test(f),
      };
    });
    fs.writeFileSync(process.argv[2], JSON.stringify(entries, null, 2) + "\n");
  ' "$files_path" "$out_path"
}
build_changed_files_json "$FILES_FILE" "$ARTIFACT_DIR/changed-files.json"

# Draft/status/branch guard — skip review unless FORCE_REVIEW=1
if [ "${FORCE_REVIEW:-0}" != "1" ]; then
  PR_IS_DRAFT=$(jq -r '.isDraft // false' "$METADATA_FILE")
  PR_STATUS=$(jq -r '.status // "active"' "$METADATA_FILE")
  PR_TARGET_REF=$(jq -r '.targetRefName // ""' "$METADATA_FILE")
  PR_TARGET_SHORT="${PR_TARGET_REF#refs/heads/}"

  if [ "$PR_IS_DRAFT" = "true" ]; then
    log "PR #$PR_ID is a draft; skipping review. Set FORCE_REVIEW=1 to override."
    printf '{"summary":"Skipped: PR is a draft.","findings":[]}\n'
    exit 0
  fi

  if [ "$PR_STATUS" != "active" ]; then
    log "PR #$PR_ID status is '$PR_STATUS' (not active); skipping review. Set FORCE_REVIEW=1 to override."
    printf '{"summary":"Skipped: PR status is %s.","findings":[]}\n' "$PR_STATUS"
    exit 0
  fi

  if [ -n "$REVIEW_TARGET_BRANCHES" ]; then
    BRANCH_ALLOWED=0
    IFS=',' read -ra ALLOWED_BRANCHES <<< "$REVIEW_TARGET_BRANCHES"
    for allowed in "${ALLOWED_BRANCHES[@]}"; do
      allowed="${allowed#refs/heads/}"
      if [ "$PR_TARGET_SHORT" = "$allowed" ]; then
        BRANCH_ALLOWED=1
        break
      fi
    done
    if [ "$BRANCH_ALLOWED" = "0" ]; then
      log "PR #$PR_ID targets branch '$PR_TARGET_SHORT' which is not in REVIEW_TARGET_BRANCHES; skipping. Set FORCE_REVIEW=1 to override."
      printf '{"summary":"Skipped: target branch %s is not in the review policy.","findings":[]}\n' "$PR_TARGET_SHORT"
      exit 0
    fi
  fi
fi
WI_CONTEXT="[]"
WI_COMMENTS_CONTEXT="[]"
THREAD_CONTEXT="[]"
if [ "$INCLUDE_WORK_ITEMS" = "1" ]; then
  WI_CONTEXT=$(jq -c '.' "$ARTIFACT_DIR/work-items.json")
  WI_COMMENTS_CONTEXT=$(jq -c '.' "$ARTIFACT_DIR/work-item-comments.json")
fi
if [ "$INCLUDE_EXISTING_COMMENTS" = "1" ]; then
  THREAD_CONTEXT=$(jq -c '.' "$ARTIFACT_DIR/threads.json")
fi
THREAD_COUNT=$(printf '%s' "$THREAD_CONTEXT" | jq 'length')
WI_COUNT=$(printf '%s' "$WI_CONTEXT" | jq 'length')
log "loaded $WI_COUNT linked work item(s) and $THREAD_COUNT existing thread(s)"

if [ "$DIFF_BYTES" -eq 0 ]; then
printf '{"summary":"No changes to review.","findings":[]}\n' > "$RAW_OUT"
else
log "running production review preflight stages"
run_pi_stage "intent reconstruction" "$REVIEW_INTENT_PROMPT_PATH" "$DIFF_FILE" "$INTENT_FILE"
run_pi_stage "context planning" "$REVIEW_CONTEXT_PLAN_PROMPT_PATH" "$DIFF_FILE" "$CONTEXT_PLAN_FILE"
collect_context_from_plan
run_pi_stage "context digest" "$REVIEW_CONTEXT_DIGEST_PROMPT_PATH" "$DIFF_FILE" "$CONTEXT_DIGEST_FILE"

build_system_prompt

if is_true "$DISABLE_CHUNK_REVIEW"; then
  if [ "$DIFF_BYTES" -gt "$CHUNK_TRIGGER_DIFF_BYTES" ]; then
    log "DISABLE_CHUNK_REVIEW is enabled; reviewing large diff in a single pass"
  fi
  run_pi_review "$DIFF_FILE" "$FILES_FILE" "$RAW_OUT" "" 0
elif [ "$DIFF_BYTES" -le "$CHUNK_TRIGGER_DIFF_BYTES" ]; then
  run_pi_review "$DIFF_FILE" "$FILES_FILE" "$RAW_OUT" "" 0
else
  log "diff exceeds chunk trigger; splitting review into file-based chunks"
  CHUNK_MANIFEST="$CHUNK_DIR/manifest.tsv"
  CURRENT_CHUNK_DIFF="$CHUNK_DIR/current.diff"
  CURRENT_CHUNK_FILES="$CHUNK_DIR/current.files"
  : > "$CHUNK_MANIFEST"
  : > "$CURRENT_CHUNK_DIFF"
  : > "$CURRENT_CHUNK_FILES"
  CURRENT_CHUNK_BYTES=0
  CHUNK_COUNT=0

  flush_chunk() {
    if [ ! -s "$CURRENT_CHUNK_FILES" ]; then
      return
    fi

    CHUNK_COUNT=$((CHUNK_COUNT + 1))
    local chunk_diff="$CHUNK_DIR/chunk-${CHUNK_COUNT}.diff"
    local chunk_files="$CHUNK_DIR/chunk-${CHUNK_COUNT}.files"
    mv "$CURRENT_CHUNK_DIFF" "$chunk_diff"
    mv "$CURRENT_CHUNK_FILES" "$chunk_files"
    printf '%s\t%s\t0\n' "$chunk_diff" "$chunk_files" >> "$CHUNK_MANIFEST"
    CURRENT_CHUNK_DIFF="$CHUNK_DIR/current.diff"
    CURRENT_CHUNK_FILES="$CHUNK_DIR/current.files"
    : > "$CURRENT_CHUNK_DIFF"
    : > "$CURRENT_CHUNK_FILES"
    CURRENT_CHUNK_BYTES=0
  }

  FILE_INDEX=0
  while IFS= read -r file; do
    [ -n "$file" ] || continue
    FILE_INDEX=$((FILE_INDEX + 1))
    FILE_DIFF_PATH="$CHUNK_DIR/file-${FILE_INDEX}.diff"
    git diff --unified=3 --no-ext-diff "$RANGE" -- "$file" > "$FILE_DIFF_PATH"
    FILE_DIFF_BYTES="$(wc -c < "$FILE_DIFF_PATH" | tr -d '[:space:]')"

    if [ "$FILE_DIFF_BYTES" -gt "$MAX_DIFF_BYTES" ]; then
      flush_chunk
      CHUNK_COUNT=$((CHUNK_COUNT + 1))
      CHUNK_DIFF_PATH="$CHUNK_DIR/chunk-${CHUNK_COUNT}.diff"
      CHUNK_FILES_PATH="$CHUNK_DIR/chunk-${CHUNK_COUNT}.files"
      head -c "$MAX_DIFF_BYTES" "$FILE_DIFF_PATH" > "$CHUNK_DIFF_PATH"
      {
      printf '\n\n'
      printf '[FILE DIFF TRUNCATED: %s original size %s bytes, cap %s bytes]\n' "$file" "$FILE_DIFF_BYTES" "$MAX_DIFF_BYTES"
      } >> "$CHUNK_DIFF_PATH"
      printf '%s\n' "$file" > "$CHUNK_FILES_PATH"
      printf '%s\t%s\t1\n' "$CHUNK_DIFF_PATH" "$CHUNK_FILES_PATH" >> "$CHUNK_MANIFEST"
      TRUNCATED=1
      continue
    fi

    if [ "$CURRENT_CHUNK_BYTES" -gt 0 ] && [ $((CURRENT_CHUNK_BYTES + FILE_DIFF_BYTES)) -gt "$MAX_DIFF_BYTES" ]; then
      flush_chunk
    fi

    cat "$FILE_DIFF_PATH" >> "$CURRENT_CHUNK_DIFF"
    printf '%s\n' "$file" >> "$CURRENT_CHUNK_FILES"
    CURRENT_CHUNK_BYTES=$((CURRENT_CHUNK_BYTES + FILE_DIFF_BYTES))
  done < "$FILES_FILE"

  flush_chunk
  [ "$CHUNK_COUNT" -gt 0 ] || die "failed to build diff chunks for review"
  log "reviewing large diff in ${CHUNK_COUNT} chunk(s)"

  CHUNK_OUTPUTS=()
  CHUNK_INDEX=0
  while IFS=$'\t' read -r chunk_diff chunk_files chunk_truncated; do
    CHUNK_INDEX=$((CHUNK_INDEX + 1))
    CHUNK_OUT="$CHUNK_DIR/chunk-${CHUNK_INDEX}.json"
    run_pi_review "$chunk_diff" "$chunk_files" "$CHUNK_OUT" "chunk ${CHUNK_INDEX}/${CHUNK_COUNT}" "$chunk_truncated"
    CHUNK_OUTPUTS+=("$CHUNK_OUT")
  done < "$CHUNK_MANIFEST"

  jq -s \
    --arg file_count "$FILE_COUNT" \
    --arg chunk_count "$CHUNK_COUNT" \
    --argjson truncated "$TRUNCATED" '
    {
      summary: (
        "Reviewed " + $file_count + " changed file(s) across " + $chunk_count + " diff chunk(s)"
        + (if $truncated == 1 then "; oversized file diffs were truncated." else "." end)
        + " "
        + ([.[].summary | select(type == "string" and length > 0)] | join(" "))
      ),
      findings: (
        [.[].findings[]?]
        | unique_by([
            (.file // ""),
            (.line // 0),
            (.severity // ""),
            (.title // ""),
            (.message // "")
          ])
      )
    }
    ' "${CHUNK_OUTPUTS[@]}" > "$RAW_OUT"
fi

cp "$RAW_OUT" "$CANDIDATE_FINDINGS_FILE"

if [ "${VERIFY_FINDINGS:-1}" != "0" ]; then
  log "running adversarial finding verification stage"
  run_pi_stage "finding verification" "$REVIEW_VERIFY_PROMPT_PATH" "$DIFF_FILE" "$VERIFIED_FINDINGS_FILE"
  cp "$VERIFIED_FINDINGS_FILE" "$RAW_OUT"
else
  log "VERIFY_FINDINGS=0; skipping verification stage"
  cp "$RAW_OUT" "$VERIFIED_FINDINGS_FILE"
fi

log "running severity calibration stage"
run_pi_stage "severity calibration" "$REVIEW_SEVERITY_PROMPT_PATH" "$DIFF_FILE" "$SEVERITY_FINDINGS_FILE"
cp "$SEVERITY_FINDINGS_FILE" "$RAW_OUT"
fi

cp "$RAW_OUT" "$FINAL_FINDINGS_FILE"
validate_review_output "$RAW_OUT"

if [ "$DRY_RUN" = "1" ]; then
log "DRY_RUN=1; printing findings JSON"
cat "$RAW_OUT"
exit 0
fi

log "posting findings to PR #$PR_ID via Python ADO helper"
python3 /app/scripts/ado_review.py post-findings \
  --org "$ADO_ORG" \
  --project "$ADO_PROJECT" \
  --repo "$ADO_REPO_ID" \
  --pr "$PR_ID" \
  --findings "$RAW_OUT" \
  --out "$ARTIFACT_DIR/posted-findings.json"
