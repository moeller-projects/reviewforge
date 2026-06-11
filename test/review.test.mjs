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
    REVIEW_STANDARDS_PATH: join(repoRoot, "standards", "clean-code.md"),
    INCLUDE_WORK_ITEMS: "0",
    INCLUDE_EXISTING_COMMENTS: "0",
    DRY_RUN: "1",
    CLONE_ROOT: root,
    STUB_PI_STDIN: piInputPath,
    STUB_FILES_CONTENT: "src/example.ts\n",
    STUB_DIFF_CONTENT: "diff --git a/src/example.ts b/src/example.ts\n+const value = 1;\n",
    STUB_PI_OUTPUT: '{"summary":"stub","findings":[]}',
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
    assert.equal(run.stdout.trim(), '{"summary":"No changes to review.","findings":[]}');
    assert.throws(() => readFileSync(run.piInputPath, "utf8"), /ENOENT/);
  } finally {
    run.cleanup();
  }
});

test("review.sh uses fixture-backed clean review output", () => {
  const expectedPath = join(fixturesDir, "clean.expected.json");
  const run = runReview({
    STUB_DIFF_CONTENT: readFileSync(join(fixturesDir, "clean.diff"), "utf8"),
    STUB_PI_OUTPUT: readFileSync(expectedPath, "utf8"),
  });

  try {
    assert.equal(run.status, 0, run.stderr);
    assert.deepEqual(JSON.parse(run.stdout), JSON.parse(readFileSync(expectedPath, "utf8")));
  } finally {
    run.cleanup();
  }
});

test("review.sh chunks large diffs by file and aggregates findings", () => {
  const expectedPath = join(fixturesDir, "large.expected.json");
  const run = runReview({
    MAX_DIFF_BYTES: "300",
    STUB_FILES_CONTENT: "src/auth.ts\nsrc/logging.ts\n",
    STUB_DIFF_CONTENT: readFileSync(join(fixturesDir, "large.diff"), "utf8"),
    STUB_DIFF_MAP: {
      "src/auth.ts": "diff --git a/src/auth.ts b/src/auth.ts\nindex 1111111..2222222 100644\n--- a/src/auth.ts\n+++ b/src/auth.ts\n@@ -8,3 +8,7 @@ export function authorize(req) {\n-  return req.user;\n+  if (!req.user) {\n+    return null;\n+  }\n+  return req.user;\n }\n",
      "src/logging.ts": "diff --git a/src/logging.ts b/src/logging.ts\nindex 3333333..4444444 100644\n--- a/src/logging.ts\n+++ b/src/logging.ts\n@@ -10,3 +10,4 @@ export function logRequest(req) {\n-  logger.info(\"request\");\n+  logger.info(\"request\", {\n+    authorization: req.headers.authorization\n+  });\n }\n",
    },
    STUB_PI_OUTPUTS: [
      '{"summary":"Auth chunk is safe.","findings":[]}',
      '{"summary":"Logging chunk exposes credentials in logs.","findings":[{"file":"src/logging.ts","line":12,"severity":"major","title":"Do not log authorization headers","message":"The new structured log payload includes the raw Authorization header. That leaks credentials into application logs and violates the clean-code standard.","suggestion":"Remove the authorization field from the log payload or replace it with a fixed boolean/redacted marker."}]}',
    ],
  });

  try {
    assert.equal(run.status, 0, run.stderr);
    assert.deepEqual(JSON.parse(run.stdout), JSON.parse(readFileSync(expectedPath, "utf8")));
    const firstInput = readFileSync(join(run.piRecordDir, "stdin-0.txt"), "utf8");
    const secondInput = readFileSync(join(run.piRecordDir, "stdin-1.txt"), "utf8");
    assert.match(run.stderr, /reviewing large diff in 2 chunk\(s\)/);
    assert.match(firstInput, /src\/auth\.ts/);
    assert.match(secondInput, /src\/logging\.ts/);
    assert.doesNotMatch(firstInput, /FILE DIFF TRUNCATED/);
  } finally {
    run.cleanup();
  }
});

test("review.sh keeps larger diffs in one pass until chunk trigger is exceeded", () => {
  const run = runReview({
    MAX_DIFF_BYTES: "300",
    CHUNK_TRIGGER_DIFF_BYTES: "1000",
    STUB_FILES_CONTENT: "src/auth.ts\nsrc/logging.ts\n",
    STUB_DIFF_CONTENT: readFileSync(join(fixturesDir, "large.diff"), "utf8"),
    STUB_PI_OUTPUTS: ['{"summary":"Full diff reviewed together.","findings":[]}'],
  });

  try {
    assert.equal(run.status, 0, run.stderr);
    assert.deepEqual(JSON.parse(run.stdout), {
      summary: "Full diff reviewed together.",
      findings: [],
    });
    const input = readFileSync(join(run.piRecordDir, "stdin-0.txt"), "utf8");
    assert.match(input, /src\/auth\.ts/);
    assert.match(input, /src\/logging\.ts/);
    assert.doesNotMatch(run.stderr, /reviewing large diff in/);
    assert.throws(() => readFileSync(join(run.piRecordDir, "stdin-1.txt"), "utf8"), /ENOENT/);
  } finally {
    run.cleanup();
  }
});
