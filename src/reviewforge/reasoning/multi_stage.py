"""Multi-stage reasoning engine (legacy fallback).

This is the original ReviewForge reasoning flow wrapped behind the
ReasoningEngine abstraction. It runs the same intent → plan → collect →
digest → review → verify → calibrate → acceptance-criteria coverage stages
as before, but produces a unified ``ReviewResult`` so the orchestrator and
posting code can stay engine-agnostic.

``multi_stage`` is kept as an explicit fallback and debugging engine. The
production default is ``single_pi``.
"""
from __future__ import annotations

import time
from typing import Any

from ..exceptions import ReasoningEngineError
from ..pipeline.schemas import (
    DiscardedFinding,
    GoodPractice,
    ModelMetadata,
    PrSummary,
    ReviewConfidence,
    ReviewMetadata,
    ReviewMetrics,
    ReviewResult,
    ReviewSummary,
    RichEvidence,
    RichFinding,
    TokenUsage,
    Uncertainty,
    VerificationSummary,
)
from ..pipeline.stage import StageContext, run_stages
from ..pipeline.stages.ac_coverage import AcceptanceCriteriaCoverageStage
from ..pipeline.stages.build_artifacts import BuildArtifactsStage
from ..pipeline.stages.calibrate_severity import CalibrateSeverityStage
from ..pipeline.stages.collect_context import CollectContextStage
from ..pipeline.stages.context_digest import ContextDigestStage
from ..pipeline.stages.plan_context import PlanContextStage
from ..pipeline.stages.reconstruct_intent import ReconstructIntentStage
from ..pipeline.stages.review_diff import ReviewDiffStage
from ..pipeline.stages.verify_findings import VerifyFindingsStage
from .engine import ReasoningEngine, register_engine


class MultiStageReasoningEngine(ReasoningEngine):
    """Run the original multi-stage review pipeline as one reasoning unit.

    This engine is the legacy fallback. It is intentionally preserved for
    debugging, benchmarking, regression comparison, and emergency fallback.
    The default production engine is :class:`SinglePiReasoningEngine`.
    """

    def __init__(self, cfg: Any | None = None) -> None:
        self._cfg = cfg

    @property
    def name(self) -> str:
        return "multi_stage"

    def execute(self, ctx: StageContext) -> ReviewResult:
        cfg = ctx.cfg
        started_at = time.time()

        stages = [
            BuildArtifactsStage(),
            ReconstructIntentStage(),
            PlanContextStage(),
            CollectContextStage(),
            ContextDigestStage(),
            ReviewDiffStage(),
            VerifyFindingsStage(),
            CalibrateSeverityStage(),
        ]
        results = run_stages(stages, ctx)
        ctx.final = ctx.severity or {"summary": "", "findings": []}
        results.extend(run_stages([AcceptanceCriteriaCoverageStage()], ctx))
        for result in results:
            if result.status == "failed":
                raise ReasoningEngineError(
                    f"reasoning stage {result.name} failed",
                    details={"error": result.error or "", "stage": result.name},
                )
        if not cfg.debug_intermediates:
            for path in (
                ctx.artifacts.intent, ctx.artifacts.plan, ctx.artifacts.collected,
                ctx.artifacts.digest, ctx.artifacts.candidate, ctx.artifacts.verified,
                ctx.artifacts.severity,
            ):
                path.unlink(missing_ok=True)

        finished_at = time.time()
        final = ctx.final or {"summary": "", "findings": []}
        findings = final.get("findings", [])
        worker_tokens = ctx.extras.get("_worker_token_usage", {})
        token_in = sum(int(r.token_usage.get("in", 0) or 0) for r in results)
        token_out = sum(int(r.token_usage.get("out", 0) or 0) for r in results)
        token_in += int(worker_tokens.get("in", 0) or 0)
        token_out += int(worker_tokens.get("out", 0) or 0)
        invocation_count = getattr(ctx.pi, "invocation_count", 0)
        repair_count = getattr(ctx.pi, "repair_invocation_count", 0)
        invocation_count = invocation_count if isinstance(invocation_count, int) else 0
        repair_count = repair_count if isinstance(repair_count, int) else 0
        result = ReviewResult(
            metadata=ReviewMetadata(
                started_at=time.strftime("%Y-%m-%dT%H:%M:%S", time.gmtime(started_at)),
                finished_at=time.strftime("%Y-%m-%dT%H:%M:%S", time.gmtime(finished_at)),
                duration_ms=int((finished_at - started_at) * 1000),
                model=ModelMetadata(
                    model=cfg.pi_model,
                    reasoning_engine=self.name,
                ),
                tokens=TokenUsage(
                    input=token_in,
                    output=token_out,
                    total=token_in + token_out,
                ),
            ),
            review_summary=ReviewSummary(summary=final.get("summary", "") or "Review completed."),
            verification_summary=VerificationSummary(
                summary="Findings verified through the multi-stage verification stage."
            ),
            pr_summary=self._build_pr_summary(ctx),
            findings=[self._legacy_to_rich(f) for f in findings],
            discarded_findings=[],
            good_practices=[],
            uncertainties=[],
            metrics=ReviewMetrics(
                changedFilesReviewed=len(getattr(ctx.state, "files", [])),
                testsRead=len(ctx.collected.get("tests", [])) if ctx.collected else 0,
                confidence="high" if ctx.cfg.verify_findings else "medium",
                reviewDepth="deep",
                piInputTokens=token_in,
                piOutputTokens=token_out,
                piTotalTokens=token_in + token_out,
                invocationCount=invocation_count,
                repairInvocationCount=repair_count,
                wallClockDurationMs=int((finished_at - started_at) * 1000),
                reasoningDurationMs=sum(r.duration_ms for r in results),
            ),
            review_confidence=ReviewConfidence(
                level="high" if ctx.cfg.verify_findings else "medium",
                reasons=["verification enabled"] if ctx.cfg.verify_findings else ["verification skipped"],
            ),
        )
        return result

    @staticmethod
    def _build_pr_summary(ctx: StageContext) -> PrSummary:
        intent = ctx.intent or {}
        digest = ctx.digest or {}
        return PrSummary(
            intent=intent.get("pr_intent", ""),
            implementation_summary=intent.get("pr_intent", ""),
            architectural_impact="\n".join(intent.get("risk_areas", [])),
            risk_assessment="\n".join(intent.get("risk_areas", [])),
            positive_observations=digest.get("possible_intentional_choices", []),
        )

    @staticmethod
    def _legacy_to_rich(f: dict[str, Any]) -> RichFinding:
        """Convert a legacy finding dict to the rich finding schema.

        The legacy schema has ``message`` and ``suggestion``; the rich schema
        splits meaning into ``observation``, ``impact``, and ``recommendation``.
        """
        ev = f.get("evidence") or {}
        evidence = RichEvidence(
            changedLines=ev.get("changedLines") or ev.get("changed_lines") or [],
            relatedFiles=ev.get("contextFilesRead") or ev.get("context_files_read") or [],
            testsRead=ev.get("testsRead") or ev.get("tests_read") or [],
            workItems=ev.get("workItems") or ev.get("work_items") or [],
            whyNewInThisPr=(
                ev.get("whyNewInThisPr")
                or ev.get("why_new_in_this_pr")
                or "Derived from the legacy finding output."
            ),
            whyNotIntentional=(
                ev.get("whyNotIntentional")
                or ev.get("why_not_intentional")
                or "The legacy verification stage retained this finding."
            ),
            classification="repository-wide" if not (f.get("file") or f.get("line")) else "",
        )
        message = f.get("message") or ""
        return RichFinding(
            title=f.get("title") or "",
            observation=message,
            impact=f.get("impact") or "This behavior can cause the reported issue.",
            recommendation=f.get("suggestion") or "Update the changed code to prevent the reported issue.",
            severity=f.get("severity") or "nit",
            confidence=f.get("confidence"),
            file=f.get("file"),
            line=f.get("line"),
            contextBasis=f.get("contextBasis") or f.get("context_basis"),
            regression=bool(f.get("regression", False)),
            evidence=evidence,
        )


register_engine(MultiStageReasoningEngine().name, MultiStageReasoningEngine)
