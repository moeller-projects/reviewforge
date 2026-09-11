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


LANGUAGE_DIRECTIVE_PREFIX = "LANGUAGE: Write every"


def language_directive(cfg: Config) -> str:
    """Return the LANGUAGE directive line for the configured review language.

    The directive tells the model to write all user-facing text (titles,
    messages, summaries, suggestions) in :attr:`Config.review_language`
    and to leave file paths, identifiers, and code untranslated.
    """
    return (
        f"\nLANGUAGE: Write every \"title\", \"message\", \"summary\", "
        f"\"suggestion\" value in {cfg.review_language}. Do NOT translate "
        "file paths, identifiers, code.\n"
    )


def _compose(source: Path, cfg: Config, *, include_standards: bool) -> str:
    """Return a prompt body plus optional standards, ending with the directive."""
    body = source.read_text(encoding="utf-8")
    if include_standards:
        body += "\n\n---\n\n" + cfg.standards_path.read_text(encoding="utf-8")
    return body + language_directive(cfg)


def system_prompt(cfg: Config) -> str:
    """Combine reviewer prompt, standards file, and the language directive.

    The language directive is appended last so it sits in the model's
    recency window after the long standards block. Smaller models tend
    to weight later instructions more heavily, and the directive was
    previously buried between two large prompt blocks where it was
    effectively ignored.
    """
    return _compose(cfg.review_prompt_path, cfg, include_standards=True)


def augment_prompt_file(
    source: Path,
    cfg: Config,
    dest: Path | None = None,
    *,
    include_standards: bool = False,
) -> Path:
    """Return a copy of ``source`` with standards and/or the language directive.

    Pi loads the system prompt from a file (see
    :func:`reviewforge.ai.runner.PiRunner._build_cmd`), and the
    per-stage prompt files (``review-system.md``, ``verify-findings.md``,
    ``severity.md`` …) are generic templates that don't know the runtime
    language. This helper writes a side-by-side copy with the runtime
    directive appended so every stage sees the same instruction. Review
    prompts additionally append the configured coding standards.

    ``dest`` defaults to ``source.with_suffix(source.suffix + ".lang")``
    in the same directory. Callers should treat the returned path as
    cached: re-calling with the same ``source`` and options returns the
    same path.
    """
    if dest is None:
        dest = source.with_name(source.name + ".lang")
    try:
        body = source.read_text(encoding="utf-8")
    except FileNotFoundError:
        # Source file doesn't exist. The runner will fail downstream when
        # Pi tries to load it, so we just return the source path as-is
        # rather than masking the error with a misleading "augmented"
        # file. This keeps test fixtures that pass a non-existent path
        # simple without changing production behavior (where the file
        # is always shipped with the image).
        return source
    if include_standards:
        body += "\n\n---\n\n" + cfg.standards_path.read_text(encoding="utf-8")
    text = body + language_directive(cfg)
    if dest.exists():
        try:
            if dest.read_text(encoding="utf-8") == text:
                return dest
        except OSError:
            pass
    dest.write_text(text, encoding="utf-8")
    return dest


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


def _session_review(intent: Path, digest: Path, chunk_label: str, truncated: bool) -> str:
    parts = [f"CHUNK LABEL: {chunk_label}"] if chunk_label else []
    extras = [f"PR intent reconstruction: {intent}" if intent.exists() else "", f"Context digest: {digest}" if digest.exists() else ""]
    extras = [item for item in extras if item]
    if extras:
        parts.append("Optional pre-digested artifacts (read with `read` tool if useful):\n  - " + "\n  - ".join(extras))
    if truncated:
        parts.append("NOTE: this chunk's diff was truncated due to size. Review only what is present and mention truncation in the summary.")
    parts.append("The chunk's unified diff is on stdin. Produce only the JSON object defined in the system prompt.")
    return "\n".join(parts) + "\n"


def _review_thread_line(thread: dict[str, Any]) -> str:
    loc = f"{thread.get('filePath')}:{thread.get('line')}" if thread.get("filePath") else "(general)"
    return f"[{thread.get('author')}] {loc}: {str(thread.get('firstComment', ''))[:300]}"
def _legacy_review(state: Any, threads: Any, intent: Path, digest: Path, chunk_label: str, truncated: bool) -> str:
    parts = [
        "Review unified diff provided on stdin.",
        "The PR range merge-base(target, source)..source.",
        f"Target branch: {state.target_branch}",
        f"Source branch: {state.source_branch}",
        f"Target commit: {state.target_commit}",
        "Existing PR comments are already listed below. Do NOT create a finding for an issue already raised in those comments.\n",
    ]
    parts.extend(_review_thread_line(thread) for thread in threads or [])
    if intent.exists():
        parts += ["---", "PR INTENT RECONSTRUCTION", intent.read_text()]
    if digest.exists():
        parts += ["---", "CONTEXT DIGEST", digest.read_text(), "Use digest evidence. If a candidate issue is plausibly intentional according to context, do not report it."]
    if truncated:
        parts.append("NOTE: diff truncated due to size. Review only what is present and mention truncation in the summary.")
    if chunk_label:
        parts.append(f"CHUNK LABEL: {chunk_label}")
    return "\n".join(parts) + "\nReturn ONLY JSON object defined in instructions.\n"


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
    del files_text, wi, wi_comments
    if cfg.pi_session_enabled:
        return _session_review(intent, digest, chunk_label, truncated)
    return _legacy_review(state, threads, intent, digest, chunk_label, truncated)


__all__ = ["review_instruction", "stage_instruction", "system_prompt"]
