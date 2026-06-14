"""Prompt assembly helpers.

These build the user-message text that the reviewer Pi CLI receives on stdin
for each pipeline stage. The system prompt itself is the on-disk file
referenced by :attr:`Config.review_prompt_path` and friends.
"""
from __future__ import annotations

from pathlib import Path
from typing import Any
import json

from ..config import Config


def system_prompt(cfg: Config) -> str:
    """Combine the reviewer prompt, the language hint, and the standards file."""
    return (
        cfg.review_prompt_path.read_text()
        + "\n\n---\n"
        + f"LANGUAGE: Write every \"title\", \"message\", \"summary\", \"suggestion\" "
          f"value in {cfg.review_language}. Do NOT translate file paths, "
          "identifiers, code.\n---\n\n"
        + cfg.standards_path.read_text()
    )


def stage_instruction(
    stage: str,
    cfg: Config,
    metadata: Path,
    files_text: str,
    wi: Any,
    threads: Any,
    paths: dict[str, Path],
) -> str:
    """Build the user-message for a "JSON-only" stage (intent, plan, …)."""
    parts: list[str] = [
        f"{stage} stage Azure DevOps PR #{cfg.pr_id}. Return only JSON object requested by system prompt.\n",
        "Repository/project metadata:",
        metadata.read_text(),
        "\nChanged files:",
        files_text,
        f"\nLinked work items:\n{json.dumps(wi, ensure_ascii=False)}",
        f"\nExisting PR comments:\n{json.dumps(threads, ensure_ascii=False)}",
    ]
    for label, key in (
        ("Intent reconstruction", "intent"),
        ("Context collection plan", "plan"),
        ("Runner-collected context", "collected"),
        ("Context digest", "digest"),
        ("Candidate findings", "candidate"),
        ("Verified findings", "verified"),
    ):
        path = paths.get(key)
        if path is not None and path.exists() and path.stat().st_size:
            parts += [f"\n{label}:", path.read_text()]
    parts.append("\nUnified diff follows on stdin.\n")
    return "\n".join(parts)


def review_instruction(
    cfg: Config,
    files_text: str,
    state: Any,
    wi: Any,
    wi_comments: Any,
    threads: Any,
    intent: Path,
    digest: Path,
    chunk_label: str = "",
    truncated: bool = False,
) -> str:
    """Build the user-message for the actual diff-review stage."""
    parts: list[str] = [
        "Review unified diff provided on stdin.",
        "The PR range merge-base(target, source)..source.",
        f"Target branch: {state.target_branch}",
        f"Source branch: {state.source_branch}",
        f"Target commit: {state.target_commit}",
        (
            "Existing PR comments are already listed below. Do NOT create a finding "
            "for an issue already raised in those comments.\n"
        ),
    ]
    for thread in threads or []:
        loc = (
            f"{thread.get('filePath')}:{thread.get('line')}"
            if thread.get("filePath") else "(general)"
        )
        parts.append(
            f"[{thread.get('author')}] {loc}: {str(thread.get('firstComment', ''))[:300]}"
        )
    if intent.exists():
        parts += ["---", "PR INTENT RECONSTRUCTION", intent.read_text()]
    if digest.exists():
        parts += [
            "---",
            "CONTEXT DIGEST",
            digest.read_text(),
            "Use digest evidence. If a candidate issue is plausibly intentional "
            "according to context, do not report it.",
        ]
    if truncated:
        parts.append(
            "NOTE: diff truncated due to size. Review only what is present and "
            "mention truncation in the summary."
        )
    if chunk_label:
        parts.append(f"CHUNK LABEL: {chunk_label}")
    return "\n".join(parts) + "\nReturn ONLY JSON object defined in instructions.\n"


__all__ = ["review_instruction", "stage_instruction", "system_prompt"]
