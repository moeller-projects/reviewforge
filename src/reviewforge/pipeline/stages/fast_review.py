"""Stage: run the entire Pi-driven review in a single call."""
from __future__ import annotations

import json
from typing import Any

from ...artifacts.builder import read_json, write_json
from ..schemas import FastReviewResult, Finding
from ..stage import Stage, StageContext
from ..validation import validate_review_doc


def _build_fast_review_instruction(ctx: StageContext) -> str:
    """Build the user message for the single-call fast review mode."""
    metadata = ctx.metadata if ctx.metadata else read_json(ctx.artifacts.metadata) or {}
    files_text = ctx.files_text or "(no changed files)"
    wi = ctx.extras.get("wi_context", [])
    threads = ctx.extras.get("thread_context", [])

    diff_text = ""
    if ctx.state is not None and ctx.state.diff_text:
        diff_text = ctx.state.diff_text
    elif ctx.artifacts.diff.exists():
        diff_text = ctx.artifacts.diff.read_text(encoding="utf-8")

    parts: list[str] = [
        f"Fast review mode for Azure DevOps PR #{ctx.cfg.pr_id}.",
        "Return only the rich JSON object defined in the system prompt.",
    ]

    if metadata:
        parts += ["\nRepository/project metadata:", json.dumps(metadata, ensure_ascii=False)]

    parts += ["\nChanged files:", files_text]

    if wi:
        parts += [f"\nLinked work items:\n{json.dumps(wi, ensure_ascii=False)}"]
    if threads:
        parts += [f"\nExisting PR comments:\n{json.dumps(threads, ensure_ascii=False)}"]

    if diff_text:
        parts += ["\nUnified diff:\n", diff_text]

    return "\n".join(parts) + "\nReturn only the rich JSON object defined in the system prompt.\n"


def _finding_to_dict(finding: Finding) -> dict[str, Any]:
    """Serialize a Pydantic Finding to a plain dict."""
    return finding.model_dump(by_alias=True, exclude_none=False)


def _normalize_finding(f: dict[str, Any]) -> dict[str, Any]:
    file = f.get("file")
    if isinstance(file, str) and file.startswith("/"):
        f["file"] = file.lstrip("/")
    return f


class FastReviewStage(Stage):
    """Single Pi call that reconstructs intent, gathers context, reviews,
    verifies, and calibrates severity. Synthesizes the canonical intermediate
    artifacts so the rest of the pipeline sees a normal layout.
    """

    name = "fast_review"

    def run(self, ctx: StageContext) -> dict[str, Any]:
        cfg = ctx.cfg
        artifacts = ctx.artifacts
        prompt_path = cfg.fast_review_prompt_path

        instruction = _build_fast_review_instruction(ctx)
        output_path = artifacts.raw_dir / "fast-review.json"

        ctx.pi.run_json(prompt_path, instruction, output_path, "fast review")
        ctx.last_token_usage = ctx.pi.last_tokens

        raw = read_json(output_path)
        if raw is None:
            raise SystemExit("[review][ERROR] fast review produced no JSON")

        result = FastReviewResult.model_validate(raw)

        # Synthesize canonical intermediate artifacts.
        intent = result.intent.model_dump()
        write_json(artifacts.intent, intent)
        ctx.intent = intent

        plan = {
            "files_to_read": result.context_summary.files_read,
            "searches_to_run": result.context_summary.searches_run,
            "tests_to_inspect": result.context_summary.tests_inspected,
        }
        write_json(artifacts.plan, plan)
        ctx.plan = plan

        collected = {
            "files_read": result.context_summary.files_read,
            "searches_run": result.context_summary.searches_run,
            "tests_inspected": result.context_summary.tests_inspected,
            "notes": result.context_summary.notes,
        }
        write_json(artifacts.collected, collected)
        ctx.collected = collected

        digest = {
            "relevant_context": [collected],
            "possible_intentional_choices": [],
            "context_gaps": [],
            "notes": result.context_summary.notes,
            "statistics": result.statistics.model_dump(),
        }
        write_json(artifacts.digest, digest)
        ctx.digest = digest

        findings = [_normalize_finding(_finding_to_dict(f)) for f in result.findings]

        candidate_doc = {"summary": result.review_summary.summary, "findings": findings}
        write_json(artifacts.candidate, candidate_doc)
        ctx.candidate = candidate_doc

        verified_doc = {"summary": result.verification_summary.summary, "findings": findings}
        write_json(artifacts.verified, verified_doc)
        ctx.verified = verified_doc

        severity_doc = {"summary": result.review_summary.summary, "findings": findings}
        write_json(artifacts.severity, severity_doc)
        ctx.severity = severity_doc

        final_summary = f"{result.review_summary.summary} {result.verification_summary.summary}".strip()
        final_doc = {"summary": final_summary, "findings": findings}
        validate_review_doc(final_doc)
        write_json(artifacts.final, final_doc)
        ctx.final = final_doc

        return {
            "findings": len(findings),
            "intent_written": str(artifacts.intent),
            "plan_written": str(artifacts.plan),
            "collected_written": str(artifacts.collected),
            "digest_written": str(artifacts.digest),
            "candidate_written": str(artifacts.candidate),
            "verified_written": str(artifacts.verified),
            "severity_written": str(artifacts.severity),
            "final_written": str(artifacts.final),
        }


__all__ = ["FastReviewStage"]
