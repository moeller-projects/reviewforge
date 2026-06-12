import test from "node:test";
import assert from "node:assert/strict";
import { mkdtempSync, mkdirSync, readFileSync, rmSync, writeFileSync } from "node:fs";
import { dirname, join } from "node:path";
import { tmpdir } from "node:os";
import { spawnSync } from "node:child_process";
import { fileURLToPath } from "node:url";

const repoRoot = dirname(dirname(fileURLToPath(import.meta.url)));
const reviewScript = join(repoRoot, "scripts", "review.sh");
const fixturesDir = join(repoRoot, "test", "fixtures", "review");

// Minimal stub outputs that satisfy each stage's JSON schema.
const STUB_INTENT = '{"pr_intent":"Stub PR intent","requirements":[],"changed_behaviors":[],"risk_areas":[],"files_requiring_context":[],"unclear_areas":[]}';
const STUB_PLAN = '{"files_to_read":[],"symbols_to_trace":[],"tests_to_inspect":[],"searches_to_run":[]}';
const STUB_DIGEST = '{"relevant_context":[],"project_conventions":[],"existing_tests":[],"possible_intentional_choices":[],"context_gaps":[]}';

function writeExecutable(path, content) {
  writeFileSync(path, content, { mode: 0o755 });
}

function createFakeBin(root) {
  const fakeBin = join(root, "fakebin");
  mkdirSync(fakeBin);

  writeExecutable(join(fakeBin, "git"), `#!/usr/bin/env bash
set -euo pipefail
cmd="\${1:-}"
shift || true
case "$cmd" in
  init|fetch)
    exit 0
    ;;
  remote)
    exit 0
    ;;
  config)
    exit 0
    ;;
  merge-base)
    printf '%s\\n' "\${STUB_BASE_COMMIT:-base123}"
    ;;
  rev-parse)
    ref="\${@: -1}"
    case "$ref" in
      *target*) printf '%s\\n' "\${STUB_TARGET_COMMIT:-target123}" ;;
      *source*) printf '%s\\n' "\${STUB_SOURCE_COMMIT:-source123}" ;;
      *) printf '%s\\n' "\${STUB_BASE_COMMIT:-base123}" ;;
    esac
    ;;
  log)
    printf 'abc1234 Stub commit\\n'
    ;;
  diff)
    target_file=""
    prev=""
    for arg in "$@"; do
      if [ "$prev" = "--" ]; then
        target_file="$arg"
        break
      fi
      prev="$arg"
    done
    for arg in "$@"; do
      if [ "$arg" = "--name-only" ]; then
        printf '%s' "\${STUB_FILES_CONTENT:-}"
        exit 0
      fi
    done
    if [ -n "$target_file" ] && [ -n "\${STUB_DIFF_MAP_PATH:-}" ]; then
      node -e '
        const fs = require("node:fs");
        const map = JSON.parse(fs.readFileSync(process.env.STUB_DIFF_MAP_PATH, "utf8"));
        process.stdout.write(map[process.argv[1]] ?? "");
      ' "$target_file"
      exit 0
    fi
    printf '%s' "\${STUB_DIFF_CONTENT:-}"
    ;;
  *)
    echo "unexpected git command: $cmd $*" >&2
    exit 1
    ;;
esac
`);

  writeExecutable(join(fakeBin, "timeout"), `#!/usr/bin/env bash
set -euo pipefail
shift
exec "$@"
`);

  writeExecutable(join(fakeBin, "pi"), `#!/usr/bin/env bash
set -euo pipefail
stdin_target="\${STUB_PI_STDIN:-}"
index=0
if [ -n "\${STUB_PI_RECORD_DIR:-}" ]; then
  mkdir -p "\${STUB_PI_RECORD_DIR}"
  count_file="\${STUB_PI_RECORD_DIR}/count"
  if [ -f "$count_file" ]; then
    index="$(cat "$count_file")"
  fi
  stdin_target="\${STUB_PI_RECORD_DIR}/stdin-\${index}.txt"
  printf '%s' "$((index + 1))" > "$count_file"
fi
if [ -n "$stdin_target" ]; then
  cat >"$stdin_target"
else
  cat >/dev/null
fi
if [ -n "\${STUB_PI_OUTPUTS_PATH:-}" ]; then
  node -e '
    const fs = require("node:fs");
    const outputs = JSON.parse(fs.readFileSync(process.env.STUB_PI_OUTPUTS_PATH, "utf8"));
    process.stdout.write(outputs[Number(process.argv[1])] ?? outputs.at(-1) ?? "");
  ' "$index"
elif [ -n "\${STUB_PI_OUTPUT+x}" ]; then
  printf '%s' "\${STUB_PI_OUTPUT}"
else
  printf '%s' '{"summary":"stub","findings":[]}'
fi
exit "\${STUB_PI_RC:-0}"
`);

  // Fake python3: intercepts ado_review.py calls and creates minimal artifact files.
  writeExecutable(join(fakeBin, "python3"), `#!/usr/bin/env bash
set -euo pipefail
cmd="\${2:-}"
if [ "$cmd" = "fetch-context" ]; then
  out=""
  prev=""
  for arg in "$@"; do
    if [ "$prev" = "--out" ]; then out="$arg"; fi
    prev="$arg"
  done
  [ -n "$out" ] || { echo "missing --out" >&2; exit 1; }
  mkdir -p "$out"
  printf '%s\\n' '{"org":"contoso","project":"payments","repositoryId":"payments-api","pullRequestId":123,"title":"Test PR","description":"","status":"active","isDraft":false,"sourceRefName":"refs/heads/feature/example","targetRefName":"refs/heads/main","createdBy":null,"reviewers":[]}' > "$out/metadata.json"
  printf '%s\\n' '[]' > "$out/work-items.json"
  printf '%s\\n' '[]' > "$out/work-item-comments.json"
  printf '%s\\n' '[]' > "$out/threads.json"
  printf '%s\\n' '{"pr":{},"workItems":[],"workItemComments":[],"existingThreads":[]}' > "$out/context.json"
elif [ "$cmd" = "post-findings" ]; then
  out=""
  prev=""
  for arg in "$@"; do
    if [ "$prev" = "--out" ]; then out="$arg"; fi
    prev="$arg"
  done
  [ -n "$out" ] && printf '%s\\n' '{"summary":"stub","parsed":0,"accepted":0,"created":0,"skipped":0,"votedWaitingForAuthor":false,"voteError":null,"posted":[]}' > "$out"
else
  exec "$(command -v python3 2>/dev/null || echo python3)" "$@"
fi
`);

  return fakeBin;
}

function runReview(overrides = {}) {
  const root = mkdtempSync(join(tmpdir(), "review-test-"));
  const fakeBin = createFakeBin(root);
  const piInputPath = join(root, "pi.stdin");
  const diffMapPath = join(root, "diff-map.json");
  const piOutputsPath = join(root, "pi-outputs.json");
  const piRecordDir = join(root, "pi-records");

  if (overrides.STUB_DIFF_MAP) {
    writeFileSync(diffMapPath, JSON.stringify(overrides.STUB_DIFF_MAP), "utf8");
  }

  if (overrides.STUB_PI_OUTPUTS) {
    writeFileSync(piOutputsPath, JSON.stringify(overrides.STUB_PI_OUTPUTS), "utf8");
  }

  const env = {
    ...process.env,
    PATH: `${fakeBin}:${process.env.PATH}`,
    ADO_MCP_AUTH_TOKEN: "test-token",
    ADO_ORG: "contoso",
    ADO_PROJECT: "payments",
    ADO_REPO_ID: "payments-api",
    PR_ID: "123",
    SOURCE_BRANCH: "feature/example",
    TARGET_BRANCH: "main",
    REVIEW_PROMPT_PATH: join(repoRoot, "prompts", "review-system.md"),
    REVIEW_INTENT_PROMPT_PATH: join(repoRoot, "prompts", "intent.md"),
    REVIEW_CONTEXT_PLAN_PROMPT_PATH: join(repoRoot, "prompts", "context-plan.md"),
    REVIEW_CONTEXT_DIGEST_PROMPT_PATH: join(repoRoot, "prompts", "context-digest.md"),
    REVIEW_VERIFY_PROMPT_PATH: join(repoRoot, "prompts", "verify-findings.md"),
    REVIEW_SEVERITY_PROMPT_PATH: join(repoRoot, "prompts", "severity.md"),
    REVIEW_STANDARDS_PATH: join(repoRoot, "standards", "clean-code.md"),
    REVIEW_ARTIFACT_DIR: join(root, "artifacts"),
    INCLUDE_WORK_ITEMS: "0",
    INCLUDE_EXISTING_COMMENTS: "0",
    DRY_RUN: "1",
    CLONE_ROOT: root,
    STUB_PI_STDIN: piInputPath,
    STUB_FILES_CONTENT: "src/example.ts\n",
    STUB_DIFF_CONTENT: "diff --git a/src/example.ts b/src/example.ts\n+const value = 1;\n",
    ...(overrides.STUB_DIFF_MAP ? { STUB_DIFF_MAP_PATH: diffMapPath } : {}),
    ...(overrides.STUB_PI_OUTPUTS ? { STUB_PI_OUTPUTS_PATH: piOutputsPath, STUB_PI_RECORD_DIR: piRecordDir } : {}),
    ...overrides,
  };

  const result = spawnSync("bash", [reviewScript], {
    cwd: repoRoot,
    env,
    encoding: "utf8",
  });

  return {
    ...result,
    root,
    piInputPath,
    piRecordDir,
    cleanup() {
      rmSync(root, { recursive: true, force: true });
    },
  };
}

test("review.sh short-circuits empty diffs without invoking pi", () => {
  const run = runReview({
    STUB_DIFF_CONTENT: "",
    STUB_FILES_CONTENT: "",
  });

  try {
    assert.equal(run.status, 0, run.stderr);
    assert.deepEqual(JSON.parse(run.stdout), { summary: "No changes to review.", findings: [] });
    assert.throws(() => readFileSync(run.piInputPath, "utf8"), /ENOENT/);
  } finally {
    run.cleanup();
  }
});

// Pipeline order for a single-pass review: intent(0), plan(1), digest(2), review(3), verify(4), severity(5)
test("review.sh uses fixture-backed clean review output", () => {
  const expectedPath = join(fixturesDir, "clean.expected.json");
  const expected = readFileSync(expectedPath, "utf8");
  const run = runReview({
    STUB_DIFF_CONTENT: readFileSync(join(fixturesDir, "clean.diff"), "utf8"),
    STUB_PI_OUTPUTS: [STUB_INTENT, STUB_PLAN, STUB_DIGEST, expected, expected, expected],
  });

  try {
    assert.equal(run.status, 0, run.stderr);
    assert.deepEqual(JSON.parse(run.stdout), JSON.parse(expected));
  } finally {
    run.cleanup();
  }
});

// Pipeline order for chunked review (2 files): intent(0), plan(1), digest(2), chunk1(3), chunk2(4), verify(5), severity(6)
test("review.sh chunks large diffs by file and aggregates findings", () => {
  const expectedPath = join(fixturesDir, "large.expected.json");
  const expected = readFileSync(expectedPath, "utf8");
  const authChunk = '{"summary":"Auth chunk is safe.","findings":[]}';
  const loggingChunk =
    '{"summary":"Logging chunk exposes credentials in logs.","findings":[{"file":"src/logging.ts","line":12,"severity":"major","title":"Do not log authorization headers","message":"The new structured log payload includes the raw Authorization header. That leaks credentials into application logs and violates the clean-code standard.","suggestion":"Remove the authorization field from the log payload or replace it with a fixed boolean/redacted marker."}]}';
  const run = runReview({
    MAX_DIFF_BYTES: "300",
    STUB_FILES_CONTENT: "src/auth.ts\nsrc/logging.ts\n",
    STUB_DIFF_CONTENT: readFileSync(join(fixturesDir, "large.diff"), "utf8"),
    STUB_DIFF_MAP: {
      "src/auth.ts": "diff --git a/src/auth.ts b/src/auth.ts\nindex 1111111..2222222 100644\n--- a/src/auth.ts\n+++ b/src/auth.ts\n@@ -8,3 +8,7 @@ export function authorize(req) {\n-  return req.user;\n+  if (!req.user) {\n+    return null;\n+  }\n+  return req.user;\n }\n",
      "src/logging.ts": "diff --git a/src/logging.ts b/src/logging.ts\nindex 3333333..4444444 100644\n--- a/src/logging.ts\n+++ b/src/logging.ts\n@@ -10,3 +10,4 @@ export function logRequest(req) {\n-  logger.info(\"request\");\n+  logger.info(\"request\", {\n+    authorization: req.headers.authorization\n+  });\n }\n",
    },
    STUB_PI_OUTPUTS: [STUB_INTENT, STUB_PLAN, STUB_DIGEST, authChunk, loggingChunk, expected, expected],
  });

  try {
    assert.equal(run.status, 0, run.stderr);
    assert.deepEqual(JSON.parse(run.stdout), JSON.parse(expected));
    // Chunks are calls 3 and 4 in the pipeline.
    const chunkOneInput = readFileSync(join(run.piRecordDir, "stdin-3.txt"), "utf8");
    const chunkTwoInput = readFileSync(join(run.piRecordDir, "stdin-4.txt"), "utf8");
    assert.match(run.stderr, /reviewing large diff in 2 chunk\(s\)/);
    assert.match(chunkOneInput, /src\/auth\.ts/);
    assert.match(chunkTwoInput, /src\/logging\.ts/);
    assert.doesNotMatch(chunkOneInput, /FILE DIFF TRUNCATED/);
  } finally {
    run.cleanup();
  }
});

// Pipeline order for single-pass with high chunk trigger: intent(0), plan(1), digest(2), review(3), verify(4), severity(5)
test("review.sh keeps larger diffs in one pass until chunk trigger is exceeded", () => {
  const singlePassOutput = '{"summary":"Full diff reviewed together.","findings":[]}';
  const run = runReview({
    MAX_DIFF_BYTES: "300",
    CHUNK_TRIGGER_DIFF_BYTES: "1000",
    STUB_FILES_CONTENT: "src/auth.ts\nsrc/logging.ts\n",
    STUB_DIFF_CONTENT: readFileSync(join(fixturesDir, "large.diff"), "utf8"),
    STUB_PI_OUTPUTS: [STUB_INTENT, STUB_PLAN, STUB_DIGEST, singlePassOutput, singlePassOutput, singlePassOutput],
  });

  try {
    assert.equal(run.status, 0, run.stderr);
    assert.deepEqual(JSON.parse(run.stdout), { summary: "Full diff reviewed together.", findings: [] });
    // Review call is index 3; full diff input should reference both files.
    const reviewInput = readFileSync(join(run.piRecordDir, "stdin-3.txt"), "utf8");
    assert.match(reviewInput, /src\/auth\.ts/);
    assert.match(reviewInput, /src\/logging\.ts/);
    assert.doesNotMatch(run.stderr, /reviewing large diff in/);
    // Only 6 Pi calls (0-5); index 6 was never written.
    assert.throws(() => readFileSync(join(run.piRecordDir, "stdin-6.txt"), "utf8"), /ENOENT/);
  } finally {
    run.cleanup();
  }
});
