// target path: pr-review-bot/scripts/post-findings.mjs
import { createHash } from "node:crypto";
import { readFileSync } from "node:fs";
import { pathToFileURL } from "node:url";
import { Client } from "@modelcontextprotocol/sdk/client/index.js";
import { StdioClientTransport } from "@modelcontextprotocol/sdk/client/stdio.js";

const SEV_RANK = { nit: 1, minor: 2, major: 3, blocker: 4 };
const SEV_LABEL = { blocker: "\u{1F534} blocker", major: "\u{1F7E0} major", minor: "\u{1F7E1} minor", nit: "\u{26AA} nit" };
const VOTE_WAITING = -5; // "waiting for author" in ADO
const MARKER = "prb";
const MAX_COMMENT_CHARS = Number(process.env.MAX_COMMENT_CHARS || 12000);

const env = (k, d) => process.env[k] ?? d;

const need = (k) => {
  const v = process.env[k];
  if (!v) {
    console.error(`[post][ERROR] ${k} is required`);
    process.exit(2);
  }
  return v;
};

function assertPositiveInt(name, value) {
  if (!Number.isInteger(value) || value <= 0) {
    throw new Error(`${name} must be a positive integer`);
  }
}

function truncateText(text, maxChars) {
  const s = String(text ?? "");
  if (s.length <= maxChars) return s;
  return `${s.slice(0, Math.max(0, maxChars - 80))}\n\n[truncated: original length ${s.length} chars]`;
}

export function extractJson(raw) {
  const t = String(raw).trim();
  return JSON.parse(t);
}

export function validate(doc) {
  if (typeof doc !== "object" || doc === null) throw new Error("contract is not an object");
  if (typeof doc.summary !== "string") throw new Error("contract.summary missing");
  if (!Array.isArray(doc.findings)) throw new Error("contract.findings must be an array");

  const clean = [];

  for (const [i, f] of doc.findings.entries()) {
    if (typeof f !== "object" || f === null) throw new Error(`finding[${i}] is not an object`);

    const sev = String(f.severity || "").toLowerCase();
    if (!SEV_RANK[sev]) throw new Error(`finding[${i}].severity invalid: ${f.severity}`);
    if (typeof f.message !== "string" || !f.message.trim()) throw new Error(`finding[${i}].message missing`);

    clean.push({
      file: f.file && typeof f.file === "string" ? f.file.replace(/^\/+/, "") : null,
      line: Number.isInteger(f.line) && f.line > 0 ? f.line : null,
      severity: sev,
      title: typeof f.title === "string" && f.title.trim() ? f.title.trim() : "Review finding",
      message: f.message.trim(),
      suggestion: typeof f.suggestion === "string" && f.suggestion.trim() ? f.suggestion.trim() : null,
    });
  }

  return {
    summary: doc.summary.trim(),
    findings: clean,
  };
}

export function keyOf(f) {
  return createHash("sha1")
    .update(`${f.file ?? ""}|${f.line ?? ""}|${f.severity}|${f.title}`)
    .digest("hex")
    .slice(0, 12);
}

function fencedCode(text) {
  const runs = String(text).match(/`+/g) ?? [];
  const longestRun = runs.reduce((max, run) => Math.max(max, run.length), 0);
  const fence = "`".repeat(Math.max(3, longestRun + 1));
  return `${fence}\n${text}\n${fence}`;
}

export function commentBody(f, key) {
  const parts = [`**${SEV_LABEL[f.severity]}** — ${f.title}`, "", truncateText(f.message, 5000)];

  if (f.suggestion) {
    parts.push("", "Suggestion:", fencedCode(truncateText(f.suggestion, 5000)));
  }

  parts.push("", `<sub>${MARKER}:${key}</sub>`);

  return truncateText(parts.join("\n"), MAX_COMMENT_CHARS);
}

export function pickArgs(tool, candidate) {
  const props = tool?.inputSchema?.properties ?? {};
  const out = {};

  for (const [k, v] of Object.entries(candidate)) {
    if (v === undefined || v === null) continue;
    if (k in props) out[k] = v;
  }

  return out;
}

function resultText(res) {
  return (res?.content ?? [])
    .filter((c) => c?.type === "text")
    .map((c) => c.text)
    .join("\n");
}

export async function main() {
  const ORG = need("ADO_ORG");
  const PROJECT = need("ADO_PROJECT");
  const REPO_ID = need("ADO_REPO_ID");
  const PR_ID = Number(need("PR_ID"));
  const TOKEN = need("ADO_MCP_AUTH_TOKEN");
  const FAIL_ON = (env("FAIL_ON", "none") || "none").toLowerCase();
  const VOTE_WAITING_ON = (env("VOTE_WAITING_ON", "major") || "major").toLowerCase();

  assertPositiveInt("PR_ID", PR_ID);

  if (FAIL_ON !== "none" && !SEV_RANK[FAIL_ON]) {
    throw new Error("FAIL_ON must be one of: none, nit, minor, major, blocker");
  }

  if (VOTE_WAITING_ON !== "none" && !SEV_RANK[VOTE_WAITING_ON]) {
    throw new Error("VOTE_WAITING_ON must be one of: none, nit, minor, major, blocker");
  }

  if (!Number.isInteger(MAX_COMMENT_CHARS) || MAX_COMMENT_CHARS < 1000) {
    throw new Error("MAX_COMMENT_CHARS must be an integer >= 1000");
  }

  const inputPath = process.argv[2] || 0;
  const raw = readFileSync(inputPath, "utf8");
  const { findings } = validate(extractJson(raw));

  console.error(`[post] parsed ${findings.length} finding(s)`);

  const transport = new StdioClientTransport({
    command: "npx",
    args: ["-y", "@azure-devops/mcp", ORG, "-d", "repositories", "--authentication", "envvar"],
    env: {
      ...process.env,
      ADO_MCP_AUTH_TOKEN: TOKEN,
    },
  });

  const client = new Client({ name: "pr-review-bot", version: "1.0.0" }, { capabilities: {} });

  let connected = false;

  try {
    await client.connect(transport);
    connected = true;

    const tools = new Map((await client.listTools()).tools.map((t) => [t.name, t]));
    const createTool = tools.get("mcp_ado_repo_create_pull_request_thread") ?? tools.get("repo_create_pull_request_thread");
    const listTool = tools.get("mcp_ado_repo_list_pull_request_threads") ?? tools.get("repo_list_pull_request_threads");
    const voteTool = tools.get("mcp_ado_repo_vote_pull_request") ?? tools.get("repo_vote_pull_request");

    if (!createTool) {
      throw new Error("repo_create_pull_request_thread not exposed by the MCP server");
    }

    let existing = "";

    if (listTool) {
      try {
        const res = await client.callTool({
          name: listTool.name,
          arguments: pickArgs(listTool, {
            repositoryId: REPO_ID,
            pullRequestId: PR_ID,
            project: PROJECT,
          }),
        });
        existing = resultText(res);
      } catch (e) {
        console.error(`[post] could not list existing threads; continuing without dedupe scan: ${e.message}`);
      }
    }

    const seen = (key) => existing.includes(`${MARKER}:${key}`);

    let created = 0;
    let skipped = 0;
    const counts = { blocker: 0, major: 0, minor: 0, nit: 0 };

    for (const f of findings) {
      counts[f.severity]++;

      const key = keyOf(f);

      if (seen(key)) {
        skipped++;
        continue;
      }

      const hasLocation = Boolean(f.file && f.line);

      const args = pickArgs(createTool, {
        repositoryId: REPO_ID,
        pullRequestId: PR_ID,
        project: PROJECT,
        content: commentBody(f, key),
        filePath: hasLocation ? `/${f.file}` : undefined,
        rightFileStartLine: hasLocation ? f.line : undefined,
        rightFileStartOffset: hasLocation ? 1 : undefined,
        rightFileEndLine: hasLocation ? f.line : undefined,
        rightFileEndOffset: hasLocation ? 1 : undefined,
      });

      await client.callTool({
        name: createTool.name,
        arguments: args,
      });

      created++;
    }

    // --- Vote "waiting for author" if threshold met and new findings were posted ---
    if (VOTE_WAITING_ON !== "none" && voteTool && created > 0) {
      const worstNew = findings
        .reduce((m, f) => Math.max(m, SEV_RANK[f.severity]), 0);

      if (worstNew >= SEV_RANK[VOTE_WAITING_ON]) {
        try {
          await client.callTool({
            name: voteTool.name,
            arguments: pickArgs(voteTool, {
              repositoryId: REPO_ID,
              pullRequestId: PR_ID,
              vote: VOTE_WAITING,
            }),
          });
          console.error(`[post] voted "waiting for author" on PR #${PR_ID} (threshold: ${VOTE_WAITING_ON})`);
        } catch (e) {
          console.error(`[post] could not vote on PR: ${e.message}`);
        }
      }
    }

    console.error(`[post] created ${created}, skipped ${skipped} already-present finding(s)`);

    if (FAIL_ON !== "none") {
      const worst = findings.reduce((m, f) => Math.max(m, SEV_RANK[f.severity]), 0);

      if (worst >= SEV_RANK[FAIL_ON]) {
        console.error(`[post] FAIL_ON=${FAIL_ON} threshold met; exiting 1`);
        process.exitCode = 1;
      }
    }
  } finally {
    if (connected) {
      try {
        await client.close();
      } catch (e) {
        console.error(`[post] warning: failed to close MCP client cleanly: ${e.message}`);
      }
    }
  }
}

if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) {
  main().catch((e) => {
    console.error(`[post][ERROR] ${e.stack || e.message}`);
    process.exit(1);
  });
}
