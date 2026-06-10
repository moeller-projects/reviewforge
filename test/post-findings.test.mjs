import test from "node:test";
import assert from "node:assert/strict";

import {
  commentBody,
  keyOf,
  shouldFailOnFindings,
  shouldVoteWaiting,
  validate,
  worstSeverityRank,
} from "../scripts/post-findings.mjs";

test("validate normalizes and sanitizes findings", () => {
  const doc = validate({
    summary: "  Summary  ",
    findings: [
      {
        file: "/src/example.ts",
        line: 12,
        severity: "MAJOR",
        title: "  Title  ",
        message: "  Body  ",
        suggestion: "  fix();  ",
      },
    ],
  });

  assert.deepEqual(doc, {
    summary: "Summary",
    findings: [
      {
        file: "src/example.ts",
        line: 12,
        severity: "major",
        title: "Title",
        message: "Body",
        suggestion: "fix();",
      },
    ],
  });
});

test("keyOf is stable for equivalent findings", () => {
  const finding = { file: "src/example.ts", line: 4, severity: "minor", title: "Keep key stable" };
  assert.equal(keyOf(finding), keyOf({ ...finding }));
});

test("commentBody includes severity, suggestion fence, and dedupe marker", () => {
  const body = commentBody(
    {
      severity: "blocker",
      title: "Reject invalid payload",
      message: "Missing validation opens a trust boundary issue.",
      suggestion: "if (!payload.id) throw new Error(`missing id`);",
    },
    "abc123",
  );

  assert.match(body, /\*\*🔴 blocker\*\* — Reject invalid payload/);
  assert.match(body, /Suggestion:/);
  assert.match(body, /````/);
  assert.match(body, /<sub>prb:abc123<\/sub>/);
});

test("severity helpers respect configured thresholds", () => {
  const findings = [
    { severity: "minor" },
    { severity: "major" },
  ];

  assert.equal(worstSeverityRank(findings), 3);
  assert.equal(shouldVoteWaiting(findings, "major"), true);
  assert.equal(shouldVoteWaiting(findings, "blocker"), false);
  assert.equal(shouldFailOnFindings(findings, "minor"), true);
  assert.equal(shouldFailOnFindings(findings, "blocker"), false);
  assert.equal(shouldFailOnFindings(findings, "none"), false);
});

test("validate rejects malformed findings", () => {
  assert.throws(() => validate({ summary: "x", findings: [{ severity: "bad", message: "oops" }] }), /severity invalid/);
});
