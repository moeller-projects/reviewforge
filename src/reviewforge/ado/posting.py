"""Idempotent ADO posting primitives.

The reviewer must not double-post when re-run on the same PR. The contract is:

* Every posted comment carries a stable marker of the form ``prb:<key>``.
* Before posting, the reviewer scans the existing PR threads for these
  markers and skips findings whose marker is already present.
* :func:`dedupe_key` is the canonical way to compute the marker from a
  finding. It is stable across reruns and tolerant of small model variation
  in non-significant fields.

This module is intentionally small and pure: no HTTP calls, no subprocess
spawning. The posting CLI imports these helpers to make decisions.
"""
from __future__ import annotations

import hashlib
import re
from dataclasses import dataclass
from typing import Any, Iterable

#: The literal marker prefix used in posted comments.
MARKER_PREFIX = "prb"

#: Regex matching a bot marker inside any comment body. Markers always appear
#: on a line of their own so they are easy to detect and to remove.
#:
#: Two forms are accepted for backward compatibility with past comments:
#:
#: * Bare form: ``prb:<key>`` (the canonical line, matches AGENTS.md §4.5).
#: * HTML-comment form: ``<!-- prb:<key> -->`` (what the default comment
#:   formatter actually emits). The leading ``<!-- `` and trailing `` -->``
#:   are tolerated so the dedupe scanner recognizes its own comments.
_MARKER_RE = re.compile(
    rf"(?m)^(?:\s*<!--\s*)?{re.escape(MARKER_PREFIX)}:([a-zA-Z0-9]{{6,32}})(?:\s*-->)?\s*$"
)

#: Field names excluded from the dedupe key. These are noisy or display-only.
_NON_SIGNIFICANT_FIELDS: frozenset[str] = frozenset(
    {"suggestion", "contextBasis", "confidence", "severity_calibration",
     "created_at", "updated_at"}
)


def _normalize_file(file: Any) -> str:
    """Normalize a file path for the dedupe key.

    Strips a leading ``/`` (common in ADO diff paths) and collapses repeated
    separators. The same file should hash identically whether it appears as
    ``src/app.ts`` or ``/src/app.ts``.
    """
    if not file:
        return ""
    return str(file).lstrip("/").replace("\\", "/")


def _normalize_evidence(evidence: Any) -> tuple[Any, ...]:
    """Reduce evidence to a stable tuple of (sorted) significant items."""
    if not isinstance(evidence, dict):
        return ()
    return tuple(sorted((str(k), str(v)) for k, v in evidence.items()))


def dedupe_key(finding: dict[str, Any]) -> str:
    """Compute a stable 12-char SHA-1 prefix identifying a finding.

    The key covers the fields that semantically define the finding:

    * ``file`` (normalized) and ``line`` — the location
    * ``severity`` — the impact
    * ``title`` — the short summary
    * ``message`` — the body

    Other fields (confidence, suggestion, evidence) are intentionally
    excluded so that minor model variation between reruns does not change
    the key.
    """
    raw = "|".join(
        [
            _normalize_file(finding.get("file")),
            str(finding.get("line") or ""),
            str(finding.get("severity") or ""),
            str(finding.get("title") or ""),
            str(finding.get("message") or ""),
        ]
    )
    return hashlib.sha1(raw.encode("utf-8")).hexdigest()[:12]


def make_marker(key: str) -> str:
    """Return the full marker text (``prb:<key>``) for a given key."""
    return f"{MARKER_PREFIX}:{key}"


def existing_bot_markers(threads: Iterable[dict[str, Any]]) -> set[str]:
    """Return the set of bot markers already present in the given PR threads.

    Only the first comment of each thread is scanned; this is how the bot
    itself posts and how other reviewers' threads are distinguishable (they
    will not contain a ``prb:`` marker on the first comment line).
    """
    markers: set[str] = set()
    for thread in threads or []:
        comments = thread.get("comments") or []
        for c in comments:
            text = c.get("content") or ""
            for match in _MARKER_RE.finditer(text):
                markers.add(match.group(1))
    return markers


def should_post(finding: dict[str, Any], existing_markers: set[str]) -> bool:
    """Return ``True`` iff a finding with this dedupe key has not been posted.

    When ``existing_markers`` is empty, the function returns ``True`` (the
    caller can still choose to skip posting for other reasons, such as
    ``dry_run``).
    """
    return dedupe_key(finding) not in existing_markers


# ---------------------------------------------------------------------------
# Stale-comment reconciliation
# ---------------------------------------------------------------------------
#
# When the source branch moves on (a new push to the PR), a finding the
# bot posted earlier may no longer anchor to a line that exists in the
# current diff. Leaving the old inline comment there is misleading: the
# reader sees a "major" finding pinned to a line that doesn't look the
# way the comment claims. The bot's job, on each run, is to find those
# threads and append a "stale" comment so the reader knows.
#
# Rule of thumb: stale iff (file, line) from the existing bot thread's
# threadContext is NOT in the current diff's per-file line set. Work
# item findings (general PR comments) have no threadContext → never
# stale by this definition. File-level anchors (no line) → never
# stale. New threads posted in this run → never stale (their anchor
# was just chosen against the current diff).


def _extract_thread_anchor(thread: dict[str, Any]) -> tuple[str | None, int | None]:
    """Return ``(file_path, line)`` from a thread's ``threadContext``.

    Returns ``(None, None)`` when the thread is a general PR comment
    (no threadContext) or a file-level anchor (no line). Both cases
    are not candidates for staleness under the current rule.
    """
    ctx = thread.get("threadContext")
    if not isinstance(ctx, dict):
        return None, None
    file_path = ctx.get("filePath")
    line = None
    right = ctx.get("rightFileStart")
    if isinstance(right, dict):
        ln = right.get("line")
        if isinstance(ln, int):
            line = ln
    if not isinstance(file_path, str) or not file_path:
        return None, None
    return file_path, line


def _thread_has_bot_marker(
    thread: dict[str, Any], existing_markers: set[str]
) -> bool:
    """Return True iff any comment in ``thread`` carries a bot marker."""
    for comment in thread.get("comments") or []:
        text = comment.get("content") or ""
        for match in _MARKER_RE.finditer(text):
            if match.group(1) in existing_markers:
                return True
    return False


def find_stale_bot_threads(
    threads: Iterable[dict[str, Any]],
    existing_markers: set[str],
    diff_anchors: dict[str, set[int]],
    *,
    just_posted_thread_ids: set[int | str] | None = None,
) -> list[dict[str, Any]]:
    """Return bot threads whose ``(file, line)`` is no longer in the current diff.

    Parameters
    ----------
    threads
        Threads as returned by :meth:`AdoClient.get_threads`.
    existing_markers
        Set of bot dedupe keys currently on the PR. Threads without a
        key in this set are human / other-bot threads and skipped.
    diff_anchors
        ``{file_path: set_of_new_file_lines}`` mapping derived from
        the current diff. Built with :meth:`DiffLineMapper.line_set`.
    just_posted_thread_ids
        Threads the bot created during this run. By definition their
        anchors match the current diff → always skipped. Optional.
    """
    just_posted = just_posted_thread_ids or set()
    stale: list[dict[str, Any]] = []
    for thread in threads:
        thread_id = thread.get("id")
        if thread_id is None or thread_id in just_posted:
            continue
        if not _thread_has_bot_marker(thread, existing_markers):
            continue
        file_path, line = _extract_thread_anchor(thread)
        if file_path is None or line is None:
            continue  # general or file-level comment → never stale
        normalized = file_path.lstrip("/")
        anchors = diff_anchors.get(normalized) or diff_anchors.get(file_path)
        if anchors is None:
            # File no longer in the diff → every line on it is stale.
            stale.append({
                "threadId": thread_id,
                "file": normalized,
                "line": line,
                "reason": "file_no_longer_in_diff",
            })
            continue
        if line not in anchors:
            stale.append({
                "threadId": thread_id,
                "file": normalized,
                "line": line,
                "reason": "line_no_longer_in_diff",
            })
    return stale


def stale_comment_body(*, short_sha: str | None = None) -> str:
    """Return the canonical body of the "stale" follow-up comment."""
    sha = (short_sha or "").strip() or "current HEAD"
    return (
        "🤖 stale — this finding no longer anchors to a line that exists in "
        f"the current diff at {sha}. The original comment is kept for "
        "audit trail; resolve or close this thread once the discussion is done."
    )


@dataclass(frozen=True)
class BotMarkers:
    """Result of classifying PR threads into bot vs. human.

    ``bot`` is the set of dedupe keys; ``human`` is the count of threads
    we did not author. The reviewer never touches human threads.
    """

    bot: set[str]
    human: int

    @property
    def count(self) -> int:
        return len(self.bot)


def classify_threads(threads: Iterable[dict[str, Any]]) -> BotMarkers:
    """Split threads into bot-authored (carrying a marker) and others."""
    bot: set[str] = set()
    human = 0
    for thread in threads or []:
        comments = thread.get("comments") or []
        marker = None
        for c in comments:
            text = c.get("content") or ""
            match = _MARKER_RE.search(text)
            if match:
                marker = match.group(1)
                break
        if marker:
            bot.add(marker)
        else:
            human += 1
    return BotMarkers(bot=bot, human=human)


def attach_marker(finding: dict[str, Any]) -> tuple[str, str]:
    """Return ``(key, marker_text)`` for a finding.

    The marker is what the poster appends to the comment body so future
    runs can detect it.
    """
    key = dedupe_key(finding)
    return key, make_marker(key)


# ---------------------------------------------------------------------------
# Work item findings — defense in depth
# ---------------------------------------------------------------------------
#
# The review prompt (``prompts/review-system.md``) instructs the model to
# create a finding with ``file: null, line: null`` whenever it reports a
# missing or unaddressed work item requirement. Work item findings are
# categorically different from code findings:
#
# * They are not anchored to a file or line.
# * They require reading the work item history, not the diff.
# * They are judged by the author against work item scope, split
#   implementations, and stale descriptions.
#
# Posting them inline (as a file comment) makes the false positive look
# authoritative. The ``prompts/review-system.md`` rule is the primary
# contract. The helpers below are defense in depth: if a model "helps"
# by guessing a file, the posting path strips it before posting so the
# finding is always a general PR comment.

#: Regex matching the canonical "Work item #N ..." title prefix the
#: review prompt requires. Matched case-insensitively and tolerant of
#: leading whitespace so a reworded title still triggers the rule.
WORK_ITEM_TITLE_RE = re.compile(r"^\s*work\s+item\s+#\d+", re.IGNORECASE)


def is_work_item_finding(finding: dict[str, Any]) -> bool:
    """Return ``True`` iff ``finding`` is a work-item-requirement finding.

    Detection is by title prefix (``Work item #<id>``) per the contract
    in ``prompts/review-system.md``. The check is intentionally strict:
    if a finding does not start with the prefix, it is treated as a
    normal code finding and routed through the standard file/line path.
    """
    title = finding.get("title") or ""
    return bool(WORK_ITEM_TITLE_RE.match(str(title)))


def as_general_comment(finding: dict[str, Any]) -> dict[str, Any]:
    """Return a copy of ``finding`` with ``file`` and ``line`` cleared.

    Use this as a defense-in-depth step for work item findings: even if
    the model guessed a file/line, the posting path strips them so the
    finding is posted as a general PR comment, not as an inline file
    comment. The original ``finding`` is not mutated.
    """
    return {**finding, "file": None, "line": None}


# Backward-compat alias used by older scripts.
DedupeKey = str  # type alias


__all__ = [
    "BotMarkers",
    "DedupeKey",
    "MARKER_PREFIX",
    "WORK_ITEM_TITLE_RE",
    "as_general_comment",
    "attach_marker",
    "classify_threads",
    "dedupe_key",
    "existing_bot_markers",
    "find_stale_bot_threads",
    "is_work_item_finding",
    "make_marker",
    "should_post",
    "stale_comment_body",
]
