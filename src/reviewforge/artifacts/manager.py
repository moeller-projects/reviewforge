"""Per-PR artifact layout.

The reviewer writes its run output to ``artifacts/pr-<PR_ID>/runs/<RUN_ID>/`` by
default. The most recent run is recorded in ``pr-<PR_ID>/latest.txt`` so callers
can find it without scanning. The set of known artifact paths is declared in
:data:`ARTIFACT_NAMES`; optional outputs may be absent when their best-effort
generation fails.
"""
from __future__ import annotations

from dataclasses import dataclass
from pathlib import Path
import os
import time

from ..config import Config

ARTIFACT_NAMES: tuple[str, ...] = (
    "metadata.json",
    "diff.patch",
    "changed-files.json",
    "commits.txt",
    "final-findings.json",
    "posted-comments.json",
    "run-summary.json",
    "review-system.combined.md",
    "work-items.json",
    "threads.json",
    "review-result.json",
    "sarif-findings.json",
    "run.log",
    "crg-analysis.json",
    "graph-context.json",
    "comment-replies.json",
    "pi-invocations.json",
    "review-scopes.json",
)


@dataclass(frozen=True)
class Artifacts:
    """Resolved paths for a single run's outputs.

    ``dir`` is the root directory. All other fields are absolute paths.
    """

    dir: Path
    run_id: str
    metadata: Path
    diff: Path
    changed_files: Path
    commits: Path
    intent: Path
    plan: Path
    collected: Path
    digest: Path
    candidate: Path
    verified: Path
    severity: Path
    final: Path
    review_result: Path
    sarif: Path
    posted: Path
    comment_replies: Path
    summary: Path
    system_prompt: Path
    raw_dir: Path
    work_items: Path
    threads: Path
    run_log: Path
    crg_analysis: Path
    graph_context: Path
    review_scopes: Path
    pi_invocations: Path

    def as_dict(self) -> dict[str, str]:
        """Return a dict mapping artifact name → absolute path string.

        Matches the order declared in :data:`ARTIFACT_NAMES` plus a few
        auxiliary files (raw dir, system prompt).
        """
        return {
            "dir": str(self.dir),
            "run_id.txt": str(self.dir / "run-id.txt"),
            "metadata.json": str(self.metadata),
            "diff.patch": str(self.diff),
            "changed-files.json": str(self.changed_files),
            "commits.txt": str(self.commits),
            "final-findings.json": str(self.final),
            "review-result.json": str(self.review_result),
            "sarif-findings.json": str(self.sarif),
            "posted-comments.json": str(self.posted),
            "comment-replies.json": str(self.comment_replies),
            "run-summary.json": str(self.summary),
            "review-system.combined.md": str(self.system_prompt),
            "work-items.json": str(self.work_items),
            "threads.json": str(self.threads),
            "run.log": str(self.run_log),
            "graph-context.json": str(self.graph_context),
            "crg-analysis.json": str(self.crg_analysis),
            "review-scopes.json": str(self.review_scopes),
            "pi-invocations.json": str(self.pi_invocations),
        }


def create(cfg: Config) -> Artifacts:
    """Create the artifact directory tree for one run and return the paths.

    The caller writes to the returned paths. The directory is created
    eagerly. If ``cfg.review_artifact_dir`` is set, that path is used verbatim
    (no per-run subdirectory, no ``latest.txt``). Otherwise the path is
    ``cfg.review_artifact_root / pr-<id> / runs / <run-id>``.
    """
    if cfg.review_artifact_dir:
        root = Path(cfg.review_artifact_dir)
        run_id = "custom"
    else:
        run_id = (
            cfg.review_run_id
            or f"{time.strftime('%Y%m%dT%H%M%SZ', time.gmtime())}-{os.getpid()}"
        )
        root = cfg.review_artifact_root / f"pr-{cfg.pr_id}" / "runs" / run_id
    root.mkdir(parents=True, exist_ok=True)

    if not cfg.review_artifact_dir:
        # ``pr-<id>/latest.txt`` points to the most recent run.
        (root.parent.parent / "latest.txt").write_text(str(root) + "\n", encoding="utf-8")

    (root / "run-id.txt").write_text(run_id + "\n", encoding="utf-8")
    # Stage runtimes write per-finding Pi outputs to ``raw/``. Create it
    # eagerly so ``Path.write_bytes`` (used by ``PiRunner.run_json``) never
    # has to materialise the parent directory on its own.
    (root / "raw").mkdir(exist_ok=True)
    return Artifacts(
        dir=root,
        run_id=run_id,
        metadata=root / "metadata.json",
        diff=root / "diff.patch",
        changed_files=root / "changed-files.json",
        commits=root / "commits.txt",
        intent=root / "raw" / "intent.json",
        plan=root / "raw" / "context-plan.json",
        collected=root / "raw" / "collected-context.json",
        digest=root / "raw" / "context-digest.json",
        candidate=root / "raw" / "candidate-findings.json",
        verified=root / "raw" / "verified-findings.json",
        severity=root / "raw" / "severity-findings.json",
        final=root / "final-findings.json",
        review_result=root / "review-result.json",
        sarif=root / "sarif-findings.json",
        posted=root / "posted-comments.json",
        comment_replies=root / "comment-replies.json",
        summary=root / "run-summary.json",
        system_prompt=root / "review-system.combined.md",
        raw_dir=root / "raw",
        work_items=root / "work-items.json",
        threads=root / "threads.json",
        run_log=root / "run.log",
        crg_analysis=root / "crg-analysis.json",
        graph_context=root / "graph-context.json",
        review_scopes=root / "review-scopes.json",
        pi_invocations=root / "pi-invocations.json",
    )


__all__ = ["ARTIFACT_NAMES", "Artifacts", "create"]
