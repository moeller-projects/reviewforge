"""Validate projected finding anchors against the current unified diff."""
from __future__ import annotations

from typing import Any

from ...ado.diff_mapper import DiffLineMapper
from ...ado.posting import is_work_item_finding
from ...artifacts.builder import write_json
from ..schemas import DiscardedFinding
from ..stage import Stage, StageContext


class ValidateAnchorsStage(Stage):
    """Downgrade or drop findings whose inline anchors are not in the diff."""

    name = "validate_anchors"

    def should_run(self, ctx: StageContext) -> bool:
        return ctx.cfg.anchor_policy != "off"

    def run(self, ctx: StageContext) -> dict[str, Any]:
        if ctx.final is None:
            return {"downgraded": 0, "dropped": 0}
        diff_text = getattr(ctx.state, "diff_text", "") or (
            ctx.artifacts.diff.read_text(encoding="utf-8") if ctx.artifacts.diff.exists() else ""
        )
        mapper = DiffLineMapper.from_text(diff_text)
        kept: list[dict[str, Any]] = []
        dropped = downgraded = 0
        dropped_keys: set[tuple[str | None, int | None, str]] = set()
        for finding in ctx.final.get("findings", []):
            if is_work_item_finding(finding) or not finding.get("file") or not finding.get("line"):
                kept.append(finding)
                continue
            valid = int(finding["line"]) in mapper.line_set(str(finding["file"]))
            if valid:
                kept.append(finding)
            elif ctx.cfg.anchor_policy == "drop":
                dropped += 1
                dropped_keys.add((finding.get("file"), finding.get("line"), str(finding.get("title", "")).casefold().strip()))
            else:
                downgraded += 1
                # Preserve the code anchor so posting can classify it as no_line_mapping.
                kept.append({**finding, "anchorDowngraded": True})
        ctx.final = {**ctx.final, "findings": kept}
        write_json(ctx.artifacts.final, ctx.final)
        if ctx.review_result is not None and dropped_keys:
            result = ctx.review_result
            retained = []
            for finding in result.findings:
                key = (finding.file, finding.line, finding.title.casefold().strip())
                if key in dropped_keys:
                    result.discarded_findings.append(
                        DiscardedFinding(reason="anchor not present in diff", category="anchor")
                    )
                else:
                    retained.append(finding)
            result.findings = retained
            write_json(ctx.artifacts.review_result, result.model_dump(by_alias=True, exclude_none=False))
        return {"downgraded": downgraded, "dropped": dropped}


__all__ = ["ValidateAnchorsStage"]
