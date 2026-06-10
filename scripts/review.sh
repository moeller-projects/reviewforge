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

: "${ADO_MCP_AUTH_TOKEN:?ADO_MCP_AUTH_TOKEN is required}"

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
and (.message | type == "string")
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

SOURCE_BRANCH="${SOURCE_BRANCH:-${SYSTEM_PULLREQUEST_SOURCEBRANCH:-}}"
TARGET_BRANCH="${TARGET_BRANCH:-${SYSTEM_PULLREQUEST_TARGETBRANCH:-}}"

# Auto-resolve branches from ADO REST API when not provided
if [ -z "$SOURCE_BRANCH" ] || [ -z "$TARGET_BRANCH" ]; then
  ADO_API_BASE_EARLY="https://dev.azure.com/$(urlencode "$ADO_ORG")/$(urlencode "$ADO_PROJECT")"
  log "auto-resolving branches from ADO REST API"
  PR_API_DATA=$(curl -sS -H "${ADO_AUTH_HEADER_PREFIX} ${ADO_AUTH_SCHEME} ${ADO_MCP_AUTH_TOKEN}" \
    -H "Accept: application/json;api-version=7.1" \
    "${ADO_API_BASE_EARLY}/_apis/git/repositories/$(urlencode "$ADO_REPO_ID")/pullRequests/${PR_ID}" 2>/dev/null || echo '{}')
  if [ -z "$SOURCE_BRANCH" ]; then
    SOURCE_BRANCH=$(printf '%s' "$PR_API_DATA" | jq -r '.sourceRefName // empty')
    [ -n "$SOURCE_BRANCH" ] || die "could not resolve source branch from API"
    log "  source branch: $SOURCE_BRANCH (from API)"
  fi
  if [ -z "$TARGET_BRANCH" ]; then
    TARGET_BRANCH=$(printf '%s' "$PR_API_DATA" | jq -r '.targetRefName // empty')
    [ -n "$TARGET_BRANCH" ] || die "could not resolve target branch from API"
    log "  target branch: $TARGET_BRANCH (from API)"
  fi
fi

SOURCE_BRANCH="${SOURCE_BRANCH#refs/heads/}"
TARGET_BRANCH="${TARGET_BRANCH#refs/heads/}"

WORKSPACE="${WORKSPACE:-/workspace}"
CLONE_ROOT="${CLONE_ROOT:-$WORKSPACE}"
REVIEW_LANGUAGE="${REVIEW_LANGUAGE:-English}"
REVIEW_PROMPT_PATH="${REVIEW_PROMPT_PATH:-/app/prompts/review-system.md}"
REVIEW_STANDARDS_PATH="${REVIEW_STANDARDS_PATH:-/app/standards/clean-code.md}"
PI_MODEL="${PI_MODEL:-openai/gpt-5.5}"
MAX_DIFF_BYTES="${MAX_DIFF_BYTES:-200000}"
PI_TIMEOUT_SECS="${PI_TIMEOUT_SECS:-600}"
DRY_RUN="${DRY_RUN:-0}"
INCLUDE_WORK_ITEMS="${INCLUDE_WORK_ITEMS:-1}"
INCLUDE_EXISTING_COMMENTS="${INCLUDE_EXISTING_COMMENTS:-1}"
VOTE_WAITING_ON="${VOTE_WAITING_ON:-major}"

require_uint MAX_DIFF_BYTES "$MAX_DIFF_BYTES"
require_uint PI_TIMEOUT_SECS "$PI_TIMEOUT_SECS"

[ -r "$REVIEW_PROMPT_PATH" ] || die "review prompt not readable: $REVIEW_PROMPT_PATH"
[ -r "$REVIEW_STANDARDS_PATH" ] || die "review standards not readable: $REVIEW_STANDARDS_PATH"

command -v git >/dev/null || die "git is required"
command -v node >/dev/null || die "node is required"
command -v pi >/dev/null || die "pi is required"
command -v jq >/dev/null || die "jq is required"
command -v curl >/dev/null || die "curl is required"

export PI_SKIP_VERSION_CHECK=1
export PI_TELEMETRY=0
export PI_OFFLINE=0
ADO_AUTH_HEADER_PREFIX="Author""ization:"
ADO_AUTH_SCHEME="Bearer"

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
cleanup_paths+=("$REPO_DIR" "$AUTH_DIR" "$DIFF_FILE" "$FILES_FILE" "$RAW_OUT" "$SYS_FILE" "$WI_FILE" "$THREADS_FILE" "$WI_COMMENTS_FILE" "$CHUNK_DIR" "${DIFF_FILE}.cut")

ADO_API_BASE="https://dev.azure.com/$(urlencode "$ADO_ORG")/$(urlencode "$ADO_PROJECT")"
REPO_URL="${ADO_API_BASE}/_git/$(urlencode "$ADO_REPO_ID")"

ado_get() {
local path="$1"
curl -sS -H "${ADO_AUTH_HEADER_PREFIX} ${ADO_AUTH_SCHEME} ${ADO_MCP_AUTH_TOKEN}" \
  -H "Accept: application/json;api-version=7.1" \
  "${ADO_API_BASE}${path}" 2>/dev/null || echo '{}'
}

ado_post_json() {
local path="$1" body="$2"
curl -sS -H "${ADO_AUTH_HEADER_PREFIX} ${ADO_AUTH_SCHEME} ${ADO_MCP_AUTH_TOKEN}" \
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

if [ "$truncated_flag" = "1" ]; then
printf 'NOTE: The diff was truncated due to size. Review only what is present and mention truncation in the summary.\n'
fi
printf 'Return ONLY the JSON object defined in your instructions.\n'
} > "$out_path"
}

run_pi_review() {
local diff_path="$1"
local files_path="$2"
local out_path="$3"
local chunk_label="${4:-}"
local truncated_flag="${5:-0}"
local instruction_path
instruction_path="$(mktemp)"
cleanup_paths+=("$instruction_path")

write_instruction "$files_path" "$instruction_path" "$chunk_label" "$truncated_flag"

log "running Pi reviewer (timeout: ${PI_TIMEOUT_SECS}s)"
set +e
timeout "$PI_TIMEOUT_SECS" env -u ADO_MCP_AUTH_TOKEN pi \
  --no-session --no-context-files --no-extensions --no-skills --no-prompt-templates \
  --tools read,grep \
  --model "$PI_MODEL" --thinking medium \
  --system-prompt "$(cat "$SYS_FILE")" \
  -p "$(cat "$instruction_path")" \
  < "$diff_path" > "$out_path"
PI_RC=$?
set -e

if [ "$PI_RC" -eq 124 ]; then
  die "Pi reviewer timed out after ${PI_TIMEOUT_SECS}s. Increase PI_TIMEOUT_SECS to allow more time."
fi
log "pi exit code: $PI_RC"
[ "$PI_RC" -eq 0 ] || die "pi exited $PI_RC"
[ -s "$out_path" ] || die "pi produced no output"
validate_review_output "$out_path"
}

GIT_ASKPASS="$AUTH_DIR/git-askpass.sh"
cat > "$GIT_ASKPASS" <<'EOF'
#!/usr/bin/env bash
case "$1" in
*Username*) printf '%s\n' "x-access-token" ;;
*) printf '%s\n' "${ADO_MCP_AUTH_TOKEN}" ;;
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

RANGE="${BASE_COMMIT}..${SOURCE_COMMIT}"

git diff --unified=3 --no-ext-diff "$RANGE" > "$DIFF_FILE"
git diff --name-only --no-ext-diff "$RANGE" > "$FILES_FILE"

DIFF_BYTES="$(wc -c < "$DIFF_FILE" | tr -d '[:space:]')"
FILE_COUNT="$(wc -l < "$FILES_FILE" | tr -d '[:space:]')"

log "changed files: $FILE_COUNT"
log "diff size: ${DIFF_BYTES} bytes"

TRUNCATED=0

# --- Fetch linked work items -----------------------------------------------
WI_CONTEXT=""
if [ "$INCLUDE_WORK_ITEMS" = "1" ]; then
log "fetching linked work items for PR #$PR_ID"
PR_DETAIL=$(ado_get "/_apis/git/repositories/$(urlencode "$ADO_REPO_ID")/pullRequests/${PR_ID}")
WI_IDS=$(printf '%s' "$PR_DETAIL" | jq -r '[.workItemRefs[]? | .id | tonumber] // []')
WI_COUNT=$(printf '%s' "$WI_IDS" | jq 'length')
log "found $WI_COUNT linked work item(s)"

if [ "$WI_COUNT" -gt 0 ]; then
  WI_BATCH=$(ado_post_json \
    "/_apis/wit/workitemsbatch" \
    "{\"ids\":${WI_IDS},\"fields\":[\"System.Title\",\"System.Description\",\"Microsoft.VSTS.Common.AcceptanceCriteria\",\"System.WorkItemType\",\"System.State\"]}")
  printf '%s' "$WI_BATCH" > "$WI_FILE"

  WI_CONTEXT=$(jq -r '
    [.value[]? | {
      id: .id,
      type: (.fields["System.WorkItemType"]?.value // "Unknown"),
      title: (.fields["System.Title"]?.value // "(untitled)"),
      state: (.fields["System.State"]?.value // ""),
      description: (.fields["System.Description"]?.value // "(none)"),
      acceptanceCriteria: (.fields["Microsoft.VSTS.Common.AcceptanceCriteria"]?.value // "(none)")
    }] // []
  ' "$WI_FILE")

  WI_COMMENTS_CONTEXT="[]"
  WI_ID_LIST=$(printf '%s' "$WI_CONTEXT" | jq -r '.[].id')
  for wid in $WI_ID_LIST; do
    log "fetching comments for work item #$wid"
    WI_COMMENTS_RAW=$(ado_get "/_apis/wit/workItems/${wid}/comments")
    WI_COMMENTS_ENTRIES=$(printf '%s' "$WI_COMMENTS_RAW" | jq -r '
      [.comments[]? | {
        id: .id,
        author: (.author?.displayName // "unknown"),
        text: (.text // "")
      }] // []
    ')
    WI_COMMENTS_COUNT=$(printf '%s' "$WI_COMMENTS_ENTRIES" | jq 'length')
    if [ "$WI_COMMENTS_COUNT" -gt 0 ]; then
      WI_COMMENTS_CONTEXT=$(printf '%s' "$WI_COMMENTS_CONTEXT" | jq \
        --arg wid "$wid" \
        --argjson comments "$WI_COMMENTS_ENTRIES" \
        '. + [{workItemId: ($wid | tonumber), comments: $comments}]')
    fi
  done
fi
fi

# --- Fetch existing PR threads (for Pi context) -----------------------------
THREAD_CONTEXT=""
if [ "$INCLUDE_EXISTING_COMMENTS" = "1" ]; then
log "fetching existing PR threads for PR #$PR_ID"
THREADS_RAW=$(ado_get "/_apis/git/repositories/$(urlencode "$ADO_REPO_ID")/pullRequests/${PR_ID}/threads")
printf '%s' "$THREADS_RAW" > "$THREADS_FILE"

THREAD_CONTEXT=$(jq -r '
  [.value[]? | select(.comments // [] | length > 0) | {
    id: .id,
    status: .status,
    filePath: (.threadContext?.filePath // null),
    line: (.threadContext?.rightFileStart?.line // null),
    firstComment: (.comments[0].content // ""),
    author: (.comments[0].author?.displayName // "unknown")
  }] // []
' "$THREADS_FILE")
THREAD_COUNT=$(printf '%s' "$THREAD_CONTEXT" | jq 'length')
log "found $THREAD_COUNT existing thread(s)"
fi

if [ "$DIFF_BYTES" -eq 0 ]; then
printf '{"summary":"No changes to review.","findings":[]}\n' > "$RAW_OUT"
else
build_system_prompt

if [ "$DIFF_BYTES" -le "$MAX_DIFF_BYTES" ]; then
  run_pi_review "$DIFF_FILE" "$FILES_FILE" "$RAW_OUT" "" 0
else
  log "diff exceeds cap; splitting review into file-based chunks"
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
fi

validate_review_output "$RAW_OUT"

if [ "$DRY_RUN" = "1" ]; then
log "DRY_RUN=1; printing findings JSON"
cat "$RAW_OUT"
exit 0
fi

log "posting findings to PR #$PR_ID"
node /app/scripts/post-findings.mjs "$RAW_OUT"
