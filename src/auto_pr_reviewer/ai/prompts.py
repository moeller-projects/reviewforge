"""Prompt assembly helpers.

These build the user-message text that the reviewer Pi CLI receives on stdin
for each pipeline stage. The system prompt itself is the on-disk file
referenced by :attr:`Config.review_prompt_path` and friends.

Token savings (Phase B of the plan):

* When ``cfg.pi_session_enabled`` is true, the model retains the
  full context from previous stages in its Pi session. So we
  shrink the per-stage user message: instead of re-embedding the
  metadata, work items, threads, and previous-stage JSON, we pass
  file paths and let the model's ``read,grep`` tools load them on
  demand.
* When ``cfg.pi_session_enabled`` is false (legacy / deterministic
  mode), we keep the old behavior: every payload is embedded
  verbatim. The pipeline still works without a session.
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


# ---------------------------------------------------------------------------
# Shared context packs (Phase B)
# ---------------------------------------------------------------------------


def _format_files_text(files_text: str) -> str:
    return files_text or "(no changed files)"


def _briefing_session(cfg: Config, paths: dict[str, Path], files_text: str) -> str:
    """One-paragraph summary sent on the first stage of a session.

    Tells the model where to find every artifact and what to expect.
    Subsequent stages can rely on the model remembering this.
    """
    metadata = paths.get("metadata")
    wi = paths.get("work_items")
    threads = paths.get("threads")
    diff = paths.get("diff")
    return (
        f"You are reviewing Azure DevOps PR #{cfg.pr_id} in session "
        f"`{cfg.pi_session_id or 'pr-' + cfg.pr_id}`. The full context is on disk:\n"
        f"  - PR metadata (title, status, branches, reviewers): {metadata}\n"
        f"  - Linked work items: {wi}\n"
        f"  - Existing PR comment threads: {threads}\n"
        f"  - Unified diff: {diff}\n"
        f"  - Changed files (one per line):\n{_format_files_text(files_text)}\n"
        "Use your `read` and `grep` tools to load whichever files you need. "
        "After you respond, this same session will be reused for the next "
        "pipeline stage — keep your final output to a single JSON object with "
        "no prose."
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
    if cfg.pi_session_enabled:
        return _briefing_session(cfg, paths, files_text)

    # Legacy / no-session path: embed everything verbatim.
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
    if cfg.pi_session_enabled:
        # In a session, the model already has the diff and metadata. The
        # chunk-specific diff is on stdin. We only need to tell it which
        # chunk it's looking at and remind it where the optional
        # pre-digested artifacts live.
        parts: list[str] = []
        if chunk_label:
            parts.append(f"CHUNK LABEL: {chunk_label}")
        if intent.exists() or digest.exists():
            extras = []
            if intent.exists():
                extras.append(f"PR intent reconstruction: {intent}")
            if digest.exists():
                extras.append(f"Context digest: {digest}")
            parts.append(
                "Optional pre-digested artifacts (read with `read` tool if useful):\n  - "
                + "\n  - ".join(extras)
            )
        if truncated:
            parts.append(
                "NOTE: this chunk's diff was truncated due to size. Review "
                "only what is present and mention truncation in the summary."
            )
        parts.append(
            "The chunk's unified diff is on stdin. Produce only the JSON "
            "object defined in the system prompt."
        )
        return "\n".join(parts) + "\n"

    # Legacy / no-session: full context in the prompt.
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
