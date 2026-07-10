"""Pipeline stage implementations.

Each module here exposes a single :class:`Stage` subclass named ``<Name>Stage``.
The orchestrator imports them and runs them in a fixed order:

    1. :class:`FetchPrMetadataStage`
    2. :class:`PrepareRepositoryStage`
    3. :class:`BuildArtifactsStage`
    4. :class:`ReconstructIntentStage`
    5. :class:`PlanContextStage`
    6. :class:`CollectContextStage`
    7. :class:`ContextDigestStage`
    8. :class:`ReviewDiffStage`
    9. :class:`VerifyFindingsStage`
    10. :class:`CalibrateSeverityStage`
    11. :class:`PostToAdoStage`
"""
from __future__ import annotations

from .ac_coverage import AcceptanceCriteriaCoverageStage
from .build_artifacts import BuildArtifactsStage
from .calibrate_severity import CalibrateSeverityStage
from .collect_context import CollectContextStage
from .context_digest import ContextDigestStage
from .fetch_pr_metadata import FetchPrMetadataStage
from .plan_context import PlanContextStage
from .post_to_ado import PostToAdoStage
from .prepare_repository import PrepareRepositoryStage
from .reconstruct_intent import ReconstructIntentStage
from .review_diff import ReviewDiffStage
from .verify_findings import VerifyFindingsStage

DEFAULT_PIPELINE: list = [
    FetchPrMetadataStage(),
    PrepareRepositoryStage(),
    BuildArtifactsStage(),
    ReconstructIntentStage(),
    PlanContextStage(),
    CollectContextStage(),
    ContextDigestStage(),
    ReviewDiffStage(),
    VerifyFindingsStage(),
    CalibrateSeverityStage(),
    AcceptanceCriteriaCoverageStage(),
    PostToAdoStage(),
]

#: Same as :data:`DEFAULT_PIPELINE` minus the final posting stage. Use this
#: for the ``review`` CLI subcommand to produce findings without posting.
REVIEW_ONLY_PIPELINE: list = [
    FetchPrMetadataStage(),
    PrepareRepositoryStage(),
    BuildArtifactsStage(),
    ReconstructIntentStage(),
    PlanContextStage(),
    CollectContextStage(),
    ContextDigestStage(),
    ReviewDiffStage(),
    VerifyFindingsStage(),
    CalibrateSeverityStage(),
    AcceptanceCriteriaCoverageStage(),
]

#: A minimal pipeline used by ``post`` to re-validate and post a previously
#: generated review. It only needs metadata and the final findings.
POST_ONLY_PIPELINE: list = [
    FetchPrMetadataStage(),
    PostToAdoStage(),
]


__all__ = [
    "AcceptanceCriteriaCoverageStage",
    "BuildArtifactsStage",
    "CalibrateSeverityStage",
    "CollectContextStage",
    "ContextDigestStage",
    "DEFAULT_PIPELINE",
    "FetchPrMetadataStage",
    "POST_ONLY_PIPELINE",
    "PlanContextStage",
    "PostToAdoStage",
    "PrepareRepositoryStage",
    "REVIEW_ONLY_PIPELINE",
    "ReconstructIntentStage",
    "ReviewDiffStage",
    "VerifyFindingsStage",
]
