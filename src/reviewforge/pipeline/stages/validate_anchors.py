"""Validate projected finding anchors against the current unified diff."""
from __future__ import annotations

from typing import Any

from ...ado.diff_mapper import DiffLineMapper
from ...ado.posting import is_work_item_finding
from ...artifacts.builder import write_json
from ..schemas import DiscardedFinding
from ..stage import Stage, StageContext


def _filter_anchor_finding(
    finding: dict[str, Any],
    ctx: StageContext,
    mapper: DiffLineMapper,
    allowed_files: set[str] | None,
) -> tuple[dict[str, Any] | None, str | None]:
    if is_work_item_finding(finding):
        return finding, None
    if (
        allowed_files is not None
        and finding.get("file")
        and str(finding["file"]).lstrip("/") not in allowed_files
    ):
        return None, "out_of_scope"
    if not finding.get("file") or not finding.get("line"):
        return finding, None
    if int(finding["line"]) in mapper.line_set(str(finding["file"])):
        return finding, None
    if ctx.cfg.anchor_policy == "drop":
        return None, "drop"
    return {**finding, "anchorDowngraded": True}, "downgrade"


def _remove_dropped_results(
    ctx: StageContext, dropped: dict[tuple[str | None, int | None, str], tuple[str, str]]
) -> None:
    if ctx.review_result is None or not dropped:
        return
    result = ctx.review_result
    retained = []
    for finding in result.findings:
        key = (finding.file, finding.line, finding.title.casefold().strip())
        if key in dropped:
            reason, category = dropped[key]
            result.discarded_findings.append(DiscardedFinding(reason=reason, category=category))
        else:
            retained.append(finding)
    result.findings = retained
    write_json(ctx.artifacts.review_result, result.model_dump(by_alias=True, exclude_none=False))

_DROP_REASONS = {
    "drop": ("anchor not present in diff", "anchor"),
    "out_of_scope": ("file not in ADO PR changes", "scope"),
}


def _apply_finding_filters(
    ctx: StageContext, mapper: DiffLineMapper, allowed_files: set[str] | None
) -> tuple[list[dict[str, Any]], dict[str, int], dict[tuple[str | None, int | None, str], tuple[str, str]]]:
    kept: list[dict[str, Any]] = []
    counts = {"drop": 0, "downgrade": 0, "out_of_scope": 0}
    dropped_keys: dict[tuple[str | None, int | None, str], tuple[str, str]] = {}
    for finding in ctx.final.get("findings", []):
        result, action = _filter_anchor_finding(finding, ctx, mapper, allowed_files)
        if result is not None:
            kept.append(result)
        if action is None:
            continue
        counts[action] += 1
        if action in _DROP_REASONS:
            key = (finding.get("file"), finding.get("line"), str(finding.get("title", "")).casefold().strip())
            dropped_keys[key] = _DROP_REASONS[action]
    return kept, counts, dropped_keys


class ValidateAnchorsStage(Stage):
    """Drop findings outside the ADO PR file set; downgrade/drop invalid anchors."""

    name = "validate_anchors"

    def should_run(self, ctx: StageContext) -> bool:
        return ctx.cfg.anchor_policy != "off"

    def run(self, ctx: StageContext) -> dict[str, Any]:
        if ctx.final is None:
            return {"downgraded": 0, "dropped": 0, "out_of_scope": 0}
        diff_text = getattr(ctx.state, "diff_text", "") or (
            ctx.artifacts.diff.read_text(encoding="utf-8") if ctx.artifacts.diff.exists() else ""
        )
        allowed = ctx.extras.get("pr_changed_files") or []
        allowed_files = {str(path).lstrip("/") for path in allowed} if allowed else None
        kept, counts, dropped_keys = _apply_finding_filters(
            ctx, DiffLineMapper.from_text(diff_text), allowed_files
        )
        ctx.final = {**ctx.final, "findings": kept}
        write_json(ctx.artifacts.final, ctx.final)
        _remove_dropped_results(ctx, dropped_keys)
        return {
            "downgraded": counts["downgrade"],
            "dropped": counts["drop"],
            "out_of_scope": counts["out_of_scope"],
        }


__all__ = ["ValidateAnchorsStage"]
