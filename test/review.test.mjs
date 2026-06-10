import test from "node:test";
import assert from "node:assert/strict";
import { mkdtempSync, mkdirSync, readFileSync, rmSync, writeFileSync } from "node:fs";
import { join } from "node:path";
import { tmpdir } from "node:os";
import { spawnSync } from "node:child_process";

const repoRoot = "/home/runner/work/auto-pr-reviewer/auto-pr-reviewer/moeller-projects/auto-pr-reviewer";
const reviewScript = join(repoRoot, "scripts", "review.sh");

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
    for arg in "$@"; do
      if [ "$arg" = "--name-only" ]; then
        printf '%s' "\${STUB_FILES_CONTENT:-}"
        exit 0
      fi
    done
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
if [ -n "\${STUB_PI_STDIN:-}" ]; then
  cat >"\${STUB_PI_STDIN}"
else
  cat >/dev/null
fi
if [ -n "\${STUB_PI_OUTPUT+x}" ]; then
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

test("review.sh truncates large diffs before invoking pi", () => {
  const run = runReview({
    MAX_DIFF_BYTES: "40",
    STUB_DIFF_CONTENT: "diff --git a/src/example.ts b/src/example.ts\n+const value = 12345;\n+const other = 67890;\n",
    STUB_PI_OUTPUT: '{"summary":"truncated diff reviewed","findings":[]}',
  });

  try {
    assert.equal(run.status, 0, run.stderr);
    assert.equal(run.stdout.trim(), '{"summary":"truncated diff reviewed","findings":[]}');
    const piInput = readFileSync(run.piInputPath, "utf8");
    assert.match(piInput, /\[DIFF TRUNCATED: original size \d+ bytes, cap 40 bytes\]/);
  } finally {
    run.cleanup();
  }
});
