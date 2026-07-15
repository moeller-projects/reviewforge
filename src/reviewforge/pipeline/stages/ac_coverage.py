"""Stage: verify the PR's diff covers each linked work item's acceptance criteria.

Runs after :class:`CalibrateSeverityStage` and before
:class:`PostToAdoStage`. For each linked work item's acceptance
criterion (AC), the stage extracts identifiers and asks: do any of
them appear in the diff? ACs with no coverage signal are appended as
general-thread findings to ``final-findings.json`` so reviewers see
them on the PR.

Disabled via ``AC_COVERAGE_CHECK=0``. The bot also skips the stage
when there are no linked work items, when there is no diff on disk,
or when dry-run is configured and ``AC_COVERAGE_DRY_RUN=0`` is set
(the default is to also annotate dry-run output so the user sees the
same coverage gaps as a real run would).
"""
from __future__ import annotations

import os
from typing import Any

from ...ado.ac_coverage import check_ac_coverage, uncovered_findings
from ...artifacts.builder import read_json, write_json
from ..stage import Stage, StageContext
from ..validation import validate_review_doc


def _log(message: str) -> None:
    print(f"[review] {message}", file=__import__("sys").stderr)


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
        uncovered = [r for r in results if not r.is_covered]
        if not uncovered:
            return {"checked": len(results), "uncovered": 0}

        findings = uncovered_findings(uncovered)
        # Append to the final review doc. The post stage reads this same
        # file, so the AC findings flow through the existing posting
        # pipeline (general-thread path, dedupe, vote, etc.).
        final = read_json(artifacts.final) if artifacts.final.exists() else None
        if final is None:
            # Backstop: copy from severity if final is missing.
            final = read_json(artifacts.severity) if artifacts.severity.exists() else {"summary": "", "findings": []}
        before = len(final.get("findings", []))
        final.setdefault("findings", []).extend(findings)
        validate_review_doc(final)
        write_json(artifacts.final, final)
        ctx.final = final

        _log(
            f"AC coverage: {len(uncovered)} uncovered of {len(results)} checked "
            f"across {len(work_items)} work item(s); appended {len(findings)} finding(s)."
        )
        return {
            "checked": len(results),
            "uncovered": len(uncovered),
            "appended": len(findings),
            "total_findings": before + len(findings),
        }


__all__ = ["AcceptanceCriteriaCoverageStage"]