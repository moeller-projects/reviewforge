// Legacy-compatible Node entrypoint for tests and callers.
// Runtime posting has migrated to scripts/ado_review.py (direct Azure DevOps REST).
import { createHash } from "node:crypto";
import { readFileSync } from "node:fs";
import { pathToFileURL } from "node:url";
import { spawnSync } from "node:child_process";

const SEV_RANK = { nit: 1, minor: 2, major: 3, blocker: 4 };
const SEV_LABEL = { blocker: "\u{1F534} blocker", major: "\u{1F7E0} major", minor: "\u{1F7E1} minor", nit: "\u{26AA} nit" };
const MARKER = "prb";
const MAX_COMMENT_CHARS = Number(process.env.MAX_COMMENT_CHARS || 12000);

function truncateText(text, maxChars) {
  const s = String(text ?? "");
  if (s.length <= maxChars) return s;
  return `${s.slice(0, Math.max(0, maxChars - 80))}\n\n[truncated: original length ${s.length} chars]`;
}

export function extractJson(raw) {
  return JSON.parse(String(raw).trim());
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

    const confidence = String(f.confidence || "").toLowerCase();
    if (confidence && !["high", "medium", "low"].includes(confidence)) {
      throw new Error(`finding[${i}].confidence invalid: ${f.confidence}`);
    }

    const normalized = {
      file: f.file && typeof f.file === "string" ? f.file.replace(/^\/+/, "") : null,
      line: Number.isInteger(f.line) && f.line > 0 ? f.line : null,
      severity: sev,
      title: typeof f.title === "string" && f.title.trim() ? f.title.trim() : "Review finding",
      message: f.message.trim(),
      confidence: confidence || null,
      suggestion: typeof f.suggestion === "string" && f.suggestion.trim() ? f.suggestion.trim() : null,
    };

    const evidence = typeof f.evidence === "object" && f.evidence !== null ? f.evidence : null;
    if (evidence) {
      normalized.evidence = {
        changedLines: Array.isArray(evidence.changed_lines) ? evidence.changed_lines.filter(Number.isInteger) : [],
        contextFilesRead: Array.isArray(evidence.context_files_read) ? evidence.context_files_read.filter((x) => typeof x === "string") : [],
        whyNewInThisPr: typeof evidence.why_new_in_this_pr === "string" ? evidence.why_new_in_this_pr.trim() : "",
        whyNotIntentional: typeof evidence.why_not_intentional === "string" ? evidence.why_not_intentional.trim() : "",
      };
    }

    clean.push(normalized);
  }

  return { summary: doc.summary.trim(), findings: clean };
}

export function keyOf(f) {
  return createHash("sha1")
    .update(`${f.file ?? ""}|${f.line ?? ""}|${f.severity}|${f.title}`)
    .digest("hex")
    .slice(0, 12);
}

export function worstSeverityRank(findings) {
  return findings.reduce((max, f) => Math.max(max, SEV_RANK[f.severity] ?? 0), 0);
}

export function shouldVoteWaiting(findings, threshold) {
  if (threshold === "none") return false;
  return worstSeverityRank(findings) >= (SEV_RANK[threshold] ?? Number.POSITIVE_INFINITY);
}

export function shouldFailOnFindings(findings, threshold) {
  if (threshold === "none") return false;
  return worstSeverityRank(findings) >= (SEV_RANK[threshold] ?? Number.POSITIVE_INFINITY);
}

function fencedCode(text) {
  const runs = String(text).match(/`+/g) ?? [];
  const longestRun = runs.reduce((max, run) => Math.max(max, run.length), 0);
  const fence = "`".repeat(Math.max(3, longestRun + 1));
  return `${fence}\n${text}\n${fence}`;
}

export function commentBody(f, key) {
  const parts = [`**${SEV_LABEL[f.severity]}** — ${f.title}`];
  if (f.confidence) parts.push(`Confidence: ${f.confidence}`);
  parts.push("", truncateText(f.message, 5000));

  if (f.evidence && (f.evidence.whyNewInThisPr || f.evidence.whyNotIntentional || f.evidence.contextFilesRead?.length)) {
    const evidenceLines = [];
    if (f.evidence.whyNewInThisPr) evidenceLines.push(`Why this is new: ${truncateText(f.evidence.whyNewInThisPr, 800)}`);
    if (f.evidence.whyNotIntentional) evidenceLines.push(`Why this does not look intentional: ${truncateText(f.evidence.whyNotIntentional, 800)}`);
    if (f.evidence.contextFilesRead?.length) evidenceLines.push(`Context checked: ${f.evidence.contextFilesRead.slice(0, 6).join(", ")}`);
    parts.push("", "Evidence:", ...evidenceLines);
  }

  if (f.suggestion) parts.push("", "Suggestion:", fencedCode(truncateText(f.suggestion, 5000)));
  parts.push("", `<sub>${MARKER}:${key}</sub>`);
  return truncateText(parts.join("\n"), MAX_COMMENT_CHARS);
}

export function pickArgs(_tool, candidate) {
  return Object.fromEntries(Object.entries(candidate).filter(([, v]) => v !== undefined && v !== null));
}

export async function main() {
  const inputPath = process.argv[2] || 0;
  // Validate early for compatibility with old failure behavior.
  validate(extractJson(readFileSync(inputPath, "utf8")));

  const org = process.env.ADO_ORG;
  const project = process.env.ADO_PROJECT;
  const repo = process.env.ADO_REPO_ID;
  const pr = process.env.PR_ID;
  if (!org || !project || !repo || !pr) {
    throw new Error("ADO_ORG, ADO_PROJECT, ADO_REPO_ID, and PR_ID are required");
  }

  const script = new URL("./ado_review.py", import.meta.url).pathname;
  const out = process.env.POST_FINDINGS_OUT || `/tmp/pr-review-posted-${pr}.json`;
  const result = spawnSync("python3", [script, "post-findings", "--org", org, "--project", project, "--repo", repo, "--pr", pr, "--findings", inputPath, "--out", out], {
    stdio: "inherit",
    env: process.env,
  });
  process.exitCode = result.status ?? 1;
}

if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) {
  main().catch((e) => {
    console.error(`[post][ERROR] ${e.stack || e.message}`);
    process.exit(1);
  });
}
