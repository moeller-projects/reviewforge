"""Idempotent ADO posting primitives.

The reviewer must not double-post when re-run on the same PR. Every posted
comment carries a ``prb:<key>`` marker. New v2 keys hash normalized file, line,
and title only: model-generated message prose and recalibrated severity drift
between runs, while title remains the semantic identity. Existing v1 keys,
which included severity and message, remain recognized during the mandatory
migration so historical bot comments still dedupe.

Marker syntax and line anchoring are separate contracts. Markers identify an
equivalent finding; stale-comment reconciliation handles anchors that move
after a push.

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

#: The marker prefix used by stale-reconciliation follow-up comments. Kept
#: distinct from :data:`MARKER_PREFIX` so a stale note is never mistaken for
#: (or mistaken as) the finding's dedupe marker.
STALE_MARKER_PREFIX = f"{MARKER_PREFIX}-stale"

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

#: Regex matching a stale-reconciliation marker inside a comment body. This is
#: deliberately a *separate* grammar from :data:`_MARKER_RE` so a stale note
#: (``prb-stale:<key>``) is never mistaken for the finding's dedupe marker and
#: vice versa. Presence of this marker on a thread is what makes a re-run
#: idempotent: once a "stale" follow-up has been appended, it is not appended
#: again.
_STALE_MARKER_RE = re.compile(
    rf"(?m)^(?:\s*<!--\s*)?{re.escape(STALE_MARKER_PREFIX)}:([a-zA-Z0-9]{{6,32}})(?:\s*-->)?\s*$"
)

#: Field names excluded from the v2 dedupe key. These are noisy or display-only.
_NON_SIGNIFICANT_FIELDS: frozenset[str] = frozenset(
    {
        "suggestion",
        "contextBasis",
        "confidence",
        "severity",
        "message",
        "severity_calibration",
        "created_at",
        "updated_at",
    }
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


def _normalize_title(title: Any) -> str:
    """Normalize title wording without treating punctuation as semantic."""
    return " ".join(re.sub(r"[\W_]+", " ", str(title or "").lower()).split())


def _normalize_evidence(evidence: Any) -> tuple[Any, ...]:
    """Reduce evidence to a stable tuple of (sorted) significant items."""
    if not isinstance(evidence, dict):
        return ()
    return tuple(sorted((str(k), str(v)) for k, v in evidence.items()))


def dedupe_key_v1(finding: dict[str, Any]) -> str:
    """Compute the historical v1 key for compatibility checks only."""
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


def dedupe_key(finding: dict[str, Any]) -> str:
    """Compute the v2 marker key from normalized location and title.

    Severity and message are deliberately excluded because they are
    model-generated or recalibrated prose that can change on rerun. Stale
    line anchors remain the responsibility of stale reconciliation.
    """
    raw = "|".join(
        [
            _normalize_file(finding.get("file")),
            str(finding.get("line") or ""),
            _normalize_title(finding.get("title")),
        ]
    )
    return hashlib.sha1(raw.encode("utf-8")).hexdigest()[:12]


def finding_fingerprint(finding: dict[str, Any]) -> str:
    """Return a rewording-tolerant file/title identity without line numbers."""
    raw = "|".join(
        [_normalize_file(finding.get("file")), _normalize_title(finding.get("title"))]
    )
    return hashlib.sha1(raw.encode("utf-8")).hexdigest()[:12]


def make_marker(key: str) -> str:
    """Return the full marker text (``prb:<key>``) for a given key."""
    return f"{MARKER_PREFIX}:{key}"


def stale_marker(key: str) -> str:
    """Return the full stale-reconciliation marker (``prb-stale:<key>``)."""
    return f"{STALE_MARKER_PREFIX}:{key}"


def existing_bot_markers(threads: Iterable[dict[str, Any]]) -> set[str]:
    """Return v1 and v2 bot markers present in the given PR threads.

    The stable marker grammar is version-agnostic, so both historical and new
    key values are collected without changing marker syntax or layout.
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
    """Return ``True`` only when neither v1 nor v2 key has been posted.

    New comments use :func:`dedupe_key` (v2); checking v1 preserves
    idempotency for comments written before the migration.
    """
    return not {
        dedupe_key_v1(finding),
        dedupe_key(finding),
    }.intersection(existing_markers)


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
#
# A thread is annotated at most once: the stale follow-up carries a
# ``prb-stale:<key>`` marker (a distinct grammar from the finding's
# ``prb:<key>`` marker), so a re-run that finds the thread stale again
# does not append a duplicate.


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


def _thread_stale_marker(thread: dict[str, Any]) -> str | None:
    """Return the stale marker key already present on a thread, if any."""
    for comment in thread.get("comments") or []:
        match = _STALE_MARKER_RE.search(comment.get("content") or "")
        if match:
            return match.group(1)
    return None


_TERMINAL_THREAD_STATUSES = frozenset({"fixed", "wontfix", "bydesign", "closed"})


def _thread_has_terminal_status(thread: dict[str, Any]) -> bool:
    """Return whether ADO reports a status that needs no further bot action."""
    return str(thread.get("status") or "").casefold() in _TERMINAL_THREAD_STATUSES


def _thread_is_actionable(thread: dict[str, Any]) -> bool:
    """Return whether a thread can still benefit from stale-anchor notice.

    A stale-anchor notice is useful only for unresolved discussions. Explicitly
    terminal ADO states are ignored; unknown or missing states remain eligible
    to preserve the existing conservative behavior.
    """
    if thread.get("isDeleted") or thread.get("thread_is_deleted"):
        return False
    return not _thread_has_terminal_status(thread)


def _stale_thread_entry(
    thread: dict[str, Any],
    existing_markers: set[str],
    diff_anchors: dict[str, set[int]],
    just_posted: set[int | str],
) -> dict[str, Any] | None:
    thread_id = thread.get("id")
    if (
        thread_id is None
        or thread_id in just_posted
        or not _thread_is_actionable(thread)
        or not _thread_has_bot_marker(thread, existing_markers)
    ):
        return None
    finding_key = _thread_marker(thread)
    # A stale follow-up for this finding already exists → annotated before.
    if finding_key is not None and _thread_stale_marker(thread) == finding_key:
        return None
    file_path, line = _extract_thread_anchor(thread)
    if file_path is None or line is None:
        return None
    normalized = file_path.lstrip("/")
    anchors = diff_anchors.get(normalized) or diff_anchors.get(file_path)
    if anchors is not None and line in anchors:
        return None
    return {
        "threadId": thread_id,
        "file": normalized,
        "line": line,
        "key": finding_key,
        "reason": "file_no_longer_in_diff" if anchors is None else "line_no_longer_in_diff",
    }


def find_stale_bot_threads(
    threads: Iterable[dict[str, Any]],
    existing_markers: set[str],
    diff_anchors: dict[str, set[int]],
    *,
    just_posted_thread_ids: set[int | str] | None = None,
) -> list[dict[str, Any]]:
    """Return bot threads whose ``(file, line)`` is no longer in the current diff.

    Threads that already carry a stale marker are skipped so a re-run does not
    append a duplicate "stale" follow-up.
    """
    just_posted = just_posted_thread_ids or set()
    stale = [
        entry
        for thread in threads
        if (entry := _stale_thread_entry(thread, existing_markers, diff_anchors, just_posted))
        is not None
    ]
    return stale




def stale_comment_body(*, short_sha: str | None = None, key: str | None = None) -> str:
    """Return the canonical stale-anchor follow-up comment.

    When ``key`` is given, the body ends with the ``prb-stale:<key>`` marker on
    its own line so a later run can tell this thread has already been
    annotated and avoid appending a duplicate.
    """
    sha = (short_sha or "").strip() or "current HEAD"
    marker_line = f"\n{stale_marker(key)}" if key else ""
    return (
        "🤖 stale anchor — this finding was posted against a line that is no "
        "longer present in the current diff at "
        f"{sha}. The original finding is preserved for context; re-evaluate "
        "it against the new code before acting."
        + marker_line
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


def _thread_marker(thread: dict[str, Any]) -> str | None:
    for comment in thread.get("comments") or []:
        match = _MARKER_RE.search(comment.get("content") or "")
        if match:
            return match.group(1)
    return None


def classify_threads(threads: Iterable[dict[str, Any]]) -> BotMarkers:
    """Split threads into bot-authored (carrying a marker) and others."""
    bot: set[str] = set()
    human = 0
    for thread in threads or []:
        marker = _thread_marker(thread)
        if marker:
            bot.add(marker)
        else:
            human += 1
    return BotMarkers(bot=bot, human=human)

# ---------------------------------------------------------------------------
# Awaiting-reply detection
# ---------------------------------------------------------------------------
#
# A bot thread awaits a reply when a human had the last word. "Bot-authored"
# is determined without an identity lookup: a comment is bot-authored when it
# carries a marker itself or its author matches the author of the thread's
# marker-carrying comment. That makes stale-reconciliation notes and previous
# bot replies count as bot-authored, so the bot never replies to itself and
# re-runs are idempotent without extra state.


def _comment_author_keys(comment: dict[str, Any]) -> set[str]:
    """Return all available author identifiers."""
    author = comment.get("author")
    if not isinstance(author, dict):
        value = str(author or comment.get("authorId") or "").strip().lower()
        return {value} if value else set()
    return {
        value.lower()
        for key in ("id", "uniqueName", "displayName")
        if (value := str(author.get(key) or "").strip())
    }


def _is_bot_comment(comment: dict[str, Any], bot_authors: set[str]) -> bool:
    if _MARKER_RE.search(comment.get("content") or ""):
        return True
    return bool(_comment_author_keys(comment) & bot_authors)


def _marker_authors(comments: Iterable[dict[str, Any]]) -> set[str]:
    """Return author keys for comments carrying a bot marker."""
    authors: set[str] = set()
    for comment in comments:
        if not _MARKER_RE.search(comment.get("content") or ""):
            continue
        authors.update(_comment_author_keys(comment))
    return authors


def _thread_awaits_reply(thread: dict[str, Any]) -> bool:
    """Return whether a single bot thread ends with a human comment."""
    comments = thread.get("comments") or []
    if not comments or not _thread_marker(thread) or _thread_has_terminal_status(thread):
        return False
    return not _is_bot_comment(comments[-1], _marker_authors(comments))


def find_awaiting_replies(threads: Iterable[dict[str, Any]]) -> list[dict[str, Any]]:
    """Return bot threads whose last comment is a human reply."""
    return [thread for thread in threads or [] if _thread_awaits_reply(thread)]


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
    "STALE_MARKER_PREFIX",
    "WORK_ITEM_TITLE_RE",
    "as_general_comment",
    "attach_marker",
    "classify_threads",
    "dedupe_key_v1",
    "existing_bot_markers",
    "find_awaiting_replies",
    "finding_fingerprint",
    "find_stale_bot_threads",
    "is_work_item_finding",
    "make_marker",
    "should_post",
    "stale_comment_body",
    "stale_marker",
]
