"""Validate projected finding anchors against the current unified diff."""
from __future__ import annotations

from typing import Any

from ...ado.diff_mapper import DiffLineMapper
from ...ado.posting import is_work_item_finding
from ...artifacts.builder import write_json
from ..schemas import DiscardedFinding
from ..stage import Stage, StageContext


def _filter_anchor_finding(
    finding: dict[str, Any], ctx: StageContext, mapper: DiffLineMapper
) -> tuple[dict[str, Any] | None, str | None]:
    if is_work_item_finding(finding) or not finding.get("file") or not finding.get("line"):
        return finding, None
    if int(finding["line"]) in mapper.line_set(str(finding["file"])):
        return finding, None
    if ctx.cfg.anchor_policy == "drop":
        return None, "drop"
    return {**finding, "anchorDowngraded": True}, "downgrade"


def _remove_dropped_results(ctx: StageContext, dropped_keys: set[tuple[str | None, int | None, str]]) -> None:
    if ctx.review_result is None or not dropped_keys:
        return
    result = ctx.review_result
    retained = []
    for finding in result.findings:
        key = (finding.file, finding.line, finding.title.casefold().strip())
        if key in dropped_keys:
            result.discarded_findings.append(DiscardedFinding(reason="anchor not present in diff", category="anchor"))
        else:
            retained.append(finding)
    result.findings = retained
    write_json(ctx.artifacts.review_result, result.model_dump(by_alias=True, exclude_none=False))


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
            result, action = _filter_anchor_finding(finding, ctx, mapper)
            if result is not None:
                kept.append(result)
            if action == "drop":
                dropped += 1
                dropped_keys.add((finding.get("file"), finding.get("line"), str(finding.get("title", "")).casefold().strip()))
            elif action == "downgrade":
                downgraded += 1
        ctx.final = {**ctx.final, "findings": kept}
        write_json(ctx.artifacts.final, ctx.final)
        _remove_dropped_results(ctx, dropped_keys)
        return {"downgraded": downgraded, "dropped": dropped}


__all__ = ["ValidateAnchorsStage"]
