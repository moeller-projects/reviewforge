from __future__ import annotations
from dataclasses import dataclass
from pathlib import Path
import os, time
from config import Config

@dataclass(frozen=True)
class Artifacts:
    dir: Path
    metadata: Path
    intent: Path
    plan: Path
    digest: Path
    candidate: Path
    verified: Path
    severity: Path
    final: Path
    collected: Path
    system_prompt: Path


def create(cfg: Config) -> Artifacts:
    if cfg.review_artifact_dir:
        root = Path(cfg.review_artifact_dir)
        run_id = "custom"
    else:
        run_id = cfg.review_run_id or f"{time.strftime('%Y%m%dT%H%M%SZ', time.gmtime())}-{os.getpid()}"
        root = cfg.review_artifact_root / f"pr-{cfg.pr_id}" / "runs" / run_id
    root.mkdir(parents=True, exist_ok=True)
    if not cfg.review_artifact_dir:
        (root.parent.parent / "latest.txt").write_text(str(root) + "\n")
    (root / "run-id.txt").write_text(run_id + "\n")
    return Artifacts(
        root,
        root / "metadata.json",
        root / "intent.json",
        root / "context-plan.json",
        root / "context-digest.json",
        root / "candidate-findings.json",
        root / "verified-findings.json",
        root / "severity-findings.json",
        root / "final-findings.json",
        root / "collected-context.json",
        root / "review-system.combined.md",
    )
