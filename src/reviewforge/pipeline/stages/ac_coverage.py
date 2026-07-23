"""Stage: verify the PR's diff covers each linked work item's acceptance criteria.

Runs after :class:`CalibrateSeverityStage` and before
:class:`PostToAdoStage`. For each linked work item's acceptance
criterion (AC), the stage extracts identifiers and asks: do any of
them appear in the diff? ACs with no coverage signal are appended as
general-thread findings to ``final-findings.json`` so reviewers see
them on the PR.

When ``AC_COVERAGE_LLM=1`` is set, the stage runs an LLM second-pass
over the uncovered ACs. The LLM can clear false positives (e.g. an AC
written in prose that the diff actually addresses), but it never adds
new gaps. The deterministic string search remains the primary filter.

Disabled via ``AC_COVERAGE_CHECK=0``. The bot also skips the stage
when there are no linked work items, when there is no diff on disk,
or when dry-run is configured and ``AC_COVERAGE_DRY_RUN=0`` is set
(the default is to also annotate dry-run output so the user sees
the same coverage gaps as a real run would).
"""
from __future__ import annotations

import os
from dataclasses import replace
from typing import Any

from ...ado.ac_coverage import AcCoverageResult, check_ac_coverage, strip_html, uncovered_findings
from ...ai.prompts import stage_instruction
from ...artifacts.builder import read_json, write_json
from ...runlog import info as _log
from ..schemas import AcCoverageLlmResult
from ..stage import Stage, StageContext
from ..validation import validate_review_doc




def _sum_tokens(a: dict[str, int], b: dict[str, int]) -> dict[str, int]:
    out = dict(a)
    for k, v in b.items():
        out[k] = out.get(k, 0) + v
    return out


class AcceptanceCriteriaCoverageStage(Stage):
    """Append ``Work item #N AC not covered`` findings for uncovered ACs."""

    name = "ac_coverage"

    def should_run(self, ctx: StageContext) -> bool:
        # User opt-out.
        if os.getenv("AC_COVERAGE_CHECK", "1") == "0":
            return False
        # Dry-run: include by default (the user wants to see what
        # would have been flagged), but allow opt-out.
        if ctx.cfg.dry_run and os.getenv("AC_COVERAGE_DRY_RUN", "1") == "0":
            return False
        return True

    def _reassess_with_llm(
        self,
        ctx: StageContext,
        uncovered: list[AcCoverageResult],
        diff_text: str,
    ) -> list[AcCoverageResult]:
        """Ask Pi to re-check uncovered ACs and clear false positives.

        The deterministic string search already flagged these as uncovered.
        The LLM is allowed to mark them covered only when it can point to
        concrete evidence in the diff. Any failure leaves the AC uncovered,
        preserving the safe failure mode.

        Returns only the ACs that are still uncovered after the LLM re-check
        (the unchecked tail plus any ACs the LLM did not clear).
        """
        cfg = ctx.cfg
        max_acs = cfg.ac_coverage_llm_max_acs
        if max_acs <= 0:
            return uncovered

        to_check = uncovered[:max_acs]
        _log(
            f"AC coverage: LLM re-checking {len(to_check)} of {len(uncovered)} "
            f"uncovered AC(s) (max={max_acs})"
        )

        base_text = stage_instruction(
            "ac coverage reassessment",
            cfg,
            ctx.artifacts.metadata,
            ctx.files_text,
            ctx.extras.get("wi_context", []),
            ctx.extras.get("thread_context", []),
            ctx.extras.get("paths", {}),
        )
        ctx.artifacts.raw_dir.mkdir(parents=True, exist_ok=True)

        still_uncovered: list[AcCoverageResult] = []
        total_tokens: dict[str, int] = {}
        cleared = 0
        for idx, result in enumerate(to_check, 1):
            ac_plain = strip_html(result.ac_text).strip()
            payload = (
                f"{base_text}\n\n"
                f"Acceptance criterion under review (work item #{result.work_item_id}):\n"
                f"{ac_plain}\n\n"
                f"Unified diff:\n{diff_text}"
            )
            out_path = ctx.artifacts.raw_dir / f"ac-coverage-{idx}.json"
            try:
                ctx.pi.run_json(
                    cfg.ac_coverage_prompt_path,
                    payload,
                    out_path,
                    f"ac coverage reassessment {idx}",
                )
                total_tokens = _sum_tokens(total_tokens, ctx.pi.last_tokens or {})
                doc = read_json(out_path) or {}
                validated = AcCoverageLlmResult.model_validate(doc)
                if validated.covered:
                    cleared += 1
                    _log(
                        f"AC coverage LLM re-check {idx}: covered "
                        f"({validated.reason or 'no reason given'})"
                    )
                else:
                    still_uncovered.append(
                        replace(
                            result,
                            llm_reassessed=True,
                            llm_reason=validated.reason,
                        )
                    )
            except BaseException as exc:
                _log(
                    f"AC coverage LLM re-check {idx} failed "
                    f"({type(exc).__name__}: {exc}); leaving uncovered"
                )
                still_uncovered.append(result)

        ctx.last_token_usage = total_tokens
        _log(f"AC coverage: LLM cleared {cleared} of {len(to_check)} checked AC(s)")
        return still_uncovered + uncovered[len(to_check):]

    def run(self, ctx: StageContext) -> dict[str, Any]:
        artifacts = ctx.artifacts
        work_items = read_json(artifacts.work_items) if artifacts.work_items.exists() else []
        diff_text = artifacts.diff.read_text(encoding="utf-8") if artifacts.diff.exists() else ""
        if not work_items:
            return {"skipped": "no work items", "uncovered": 0}
        if not diff_text:
            return {"skipped": "no diff on disk", "uncovered": 0}

        results = check_ac_coverage(
            work_items,
            diff_text,
            [f.get("file", "") for f in (read_json(artifacts.changed_files) or [])],
        )
        all_results = results
        uncovered = [r for r in results if not r.is_covered]
        if not uncovered:
            return {"checked": len(all_results), "uncovered": 0}

        if ctx.cfg.ac_coverage_llm:
            uncovered = self._reassess_with_llm(ctx, uncovered, diff_text)

        if not uncovered:
            return {
                "checked": len(all_results),
                "uncovered": 0,
                "llm_reassessed": ctx.cfg.ac_coverage_llm,
            }

        findings = uncovered_findings(uncovered)
        # Keep AC findings in the postable document held on the context.
        final = ctx.final or {"summary": "", "findings": []}
        before = len(final.get("findings", []))
        final.setdefault("findings", []).extend(findings)
        validate_review_doc(final)
        ctx.final = final

        _log(
            f"AC coverage: {len(uncovered)} uncovered of {len(all_results)} checked "
            f"across {len(work_items)} work item(s); appended {len(findings)} finding(s)."
        )
        return {
            "checked": len(all_results),
            "uncovered": len(uncovered),
            "appended": len(findings),
            "total_findings": before + len(findings),
            "llm_reassessed": ctx.cfg.ac_coverage_llm,
        }


__all__ = ["AcceptanceCriteriaCoverageStage"]
