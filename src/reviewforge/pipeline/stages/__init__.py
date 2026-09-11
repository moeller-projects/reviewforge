"""Pipeline stage implementations.

Each module here exposes a single :class:`Stage` subclass named ``<Name>Stage``.
The orchestrator imports them and runs them in a fixed order:

    1. :class:`FetchPrMetadataStage`
    2. :class:`PrepareRepositoryStage`
    3. :class:`ExecuteReasoningEngineStage`
    4. :class:`ValidateAnchorsStage`
    5. :class:`PostToAdoStage`
The Pi-driven review work itself lives in ``reviewforge.reasoning`` engines.
"""
from __future__ import annotations

from .ac_coverage import AcceptanceCriteriaCoverageStage
from .build_artifacts import BuildArtifactsStage
from .calibrate_severity import CalibrateSeverityStage
from .collect_context import CollectContextStage
from .context_digest import ContextDigestStage
from ..crg import EnrichWithCrgStage
from .execute_reasoning_engine import ExecuteReasoningEngineStage
from .detect_review_mode import DetectReviewModeStage
from .fetch_pr_metadata import FetchPrMetadataStage
from .plan_context import PlanContextStage
from .post_to_ado import PostToAdoStage
from .prepare_repository import PrepareRepositoryStage
from .reply_to_comments import ReplyToCommentsStage
from .reconstruct_intent import ReconstructIntentStage
from .review_diff import ReviewDiffStage
from .verify_findings import VerifyFindingsStage
from .validate_anchors import ValidateAnchorsStage

DEFAULT_PIPELINE: list = [
    FetchPrMetadataStage(),
    PrepareRepositoryStage(),
    EnrichWithCrgStage(),
    ExecuteReasoningEngineStage(),
    ValidateAnchorsStage(),
    PostToAdoStage(),
    ReplyToCommentsStage(),
]
#: Minimal pipeline for ``reviewforge reply``: fetch context, prepare the
#: checkout (Pi reads code while drafting), then answer pending replies.
#: No new findings are generated or posted.
REPLY_PIPELINE: list = [
    FetchPrMetadataStage(),
    PrepareRepositoryStage(),
    ReplyToCommentsStage(),
]

#: Same as :data:`DEFAULT_PIPELINE` minus the final posting stage. Use this
#: for the ``review`` CLI subcommand to produce findings without posting.
REVIEW_ONLY_PIPELINE: list = [
    FetchPrMetadataStage(),
    PrepareRepositoryStage(),
    EnrichWithCrgStage(),
    ExecuteReasoningEngineStage(),
    ValidateAnchorsStage(),
]

#: A minimal pipeline used by ``post`` to re-validate and post a previously
#: generated review. It only needs metadata and the final findings.
POST_ONLY_PIPELINE: list = [
    FetchPrMetadataStage(),
    PostToAdoStage(),
]

#: Legacy alias kept for callers that import the old fast-review pipeline.
FAST_REVIEW_PIPELINE: list = [
    FetchPrMetadataStage(),
    PrepareRepositoryStage(),
    EnrichWithCrgStage(),
    ExecuteReasoningEngineStage(),
    ValidateAnchorsStage(),
    PostToAdoStage(),
]

FAST_REVIEW_REVIEW_ONLY_PIPELINE: list = [
    FetchPrMetadataStage(),
    PrepareRepositoryStage(),
    EnrichWithCrgStage(),
    ExecuteReasoningEngineStage(),
    ValidateAnchorsStage(),
]

__all__ = [
    "AcceptanceCriteriaCoverageStage",
    "BuildArtifactsStage",
    "CalibrateSeverityStage",
    "CollectContextStage",
    "DetectReviewModeStage",
    "DEFAULT_PIPELINE",
    "EnrichWithCrgStage",
    "ExecuteReasoningEngineStage",
    "FAST_REVIEW_PIPELINE",
    "FAST_REVIEW_REVIEW_ONLY_PIPELINE",
    "FetchPrMetadataStage",
    "POST_ONLY_PIPELINE",
    "PlanContextStage",
    "PostToAdoStage",
    "PrepareRepositoryStage",
    "REVIEW_ONLY_PIPELINE",
    "REPLY_PIPELINE",
    "ReplyToCommentsStage",
    "ReconstructIntentStage",
    "ReviewDiffStage",
    "VerifyFindingsStage",
    "ValidateAnchorsStage",
]
