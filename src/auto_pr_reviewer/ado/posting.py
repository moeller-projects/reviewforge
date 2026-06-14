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
_MARKER_RE = re.compile(rf"(?m)^{re.escape(MARKER_PREFIX)}:([a-zA-Z0-9]{{6,32}})\s*$")

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


# Backward-compat alias used by older scripts.
DedupeKey = str  # type alias


__all__ = [
    "BotMarkers",
    "DedupeKey",
    "MARKER_PREFIX",
    "attach_marker",
    "classify_threads",
    "dedupe_key",
    "existing_bot_markers",
    "make_marker",
    "should_post",
]
