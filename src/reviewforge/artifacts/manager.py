"""Per-PR artifact layout.

The reviewer writes its run output to ``artifacts/pr-<PR_ID>/runs/<RUN_ID>/`` by
default. The most recent run is recorded in ``pr-<PR_ID>/latest.txt`` so callers
can find it without scanning. The set of files written by the pipeline is
declared in :data:`ARTIFACT_NAMES` and treated as a stable contract.
"""
from __future__ import annotations

from dataclasses import dataclass
from pathlib import Path
import os
import time

from ..config import Config

#: Stable contract: every well-formed run produces these files.
ARTIFACT_NAMES: tuple[str, ...] = (
    "metadata.json",
    "diff.patch",
    "changed-files.json",
    "commits.txt",
    "intent.json",
    "context-plan.json",
    "collected-context.json",
    "context-digest.json",
    "candidate-findings.json",
    "verified-findings.json",
    "severity-findings.json",
    "final-findings.json",
    "posted-comments.json",
    "run-summary.json",
    "review-system.combined.md",
    "work-items.json",
    "threads.json",
    "review-result.json",
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
    posted: Path
    summary: Path
    system_prompt: Path
    raw_dir: Path
    work_items: Path
    threads: Path

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
            "intent.json": str(self.intent),
            "context-plan.json": str(self.plan),
            "collected-context.json": str(self.collected),
            "context-digest.json": str(self.digest),
            "candidate-findings.json": str(self.candidate),
            "verified-findings.json": str(self.verified),
            "severity-findings.json": str(self.severity),
            "final-findings.json": str(self.final),
            "review-result.json": str(self.review_result),
            "posted-comments.json": str(self.posted),
            "run-summary.json": str(self.summary),
            "review-system.combined.md": str(self.system_prompt),
            "work-items.json": str(self.work_items),
            "threads.json": str(self.threads),
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
        intent=root / "intent.json",
        plan=root / "context-plan.json",
        collected=root / "collected-context.json",
        digest=root / "context-digest.json",
        candidate=root / "candidate-findings.json",
        verified=root / "verified-findings.json",
        severity=root / "severity-findings.json",
        final=root / "final-findings.json",
        review_result=root / "review-result.json",
        posted=root / "posted-comments.json",
        summary=root / "run-summary.json",
        system_prompt=root / "review-system.combined.md",
        raw_dir=root / "raw",
        work_items=root / "work-items.json",
        threads=root / "threads.json",
    )


__all__ = ["ARTIFACT_NAMES", "Artifacts", "create"]
