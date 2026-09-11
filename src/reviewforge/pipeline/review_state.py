"""Deterministic review history normalization and mode selection."""
from __future__ import annotations

from dataclasses import dataclass
from datetime import datetime, timezone
from enum import Enum
import re
from typing import Any

from ..ado.posting import finding_fingerprint


class ReviewMode(str, Enum):
    INITIAL = "initial"
    FOLLOW_UP = "follow_up"
    NO_OP = "no_op"
    FORCE_FULL = "force_full"


@dataclass(frozen=True)
class ReviewerIdentity:
    user_id: str
    display_name: str = ""
    unique_name: str = ""
    descriptor: str = ""


@dataclass(frozen=True)
class ReviewComment:
    comment_id: str | int | None
    author_id: str
    author_name: str
    text: str
    published_at: str
    commit_id: str | None = None
    status: str = ""
    thread_id: str | int | None = None
    file_path: str | None = None
    line: int | None = None


@dataclass(frozen=True)
class FeedbackEntry:
    fingerprint: str
    thread_status: str
    last_author_reply: str = ""
    disposition: str = "unresolved"
    thread_id: str | int | None = None

@dataclass(frozen=True)
class ReviewState:
    reviewer: ReviewerIdentity | None
    mode: ReviewMode
    last_review_at: str | None = None
    last_reviewed_commit: str | None = None
    previous_comments: tuple[ReviewComment, ...] = ()
    active_comments: tuple[ReviewComment, ...] = ()
    feedback: tuple[FeedbackEntry, ...] = ()
    resolved_comments: tuple[ReviewComment, ...] = ()
    changed_commits: tuple[str, ...] = ()
    changed_files: tuple[str, ...] = ()
    reason: str = ""

    def as_context(self) -> dict[str, Any]:
        def comment(c: ReviewComment) -> dict[str, Any]:
            return {
                "id": c.comment_id,
                "authorId": c.author_id,
                "author": c.author_name,
                "text": c.text,
                "publishedAt": c.published_at,
                "commitId": c.commit_id,
                "threadId": c.thread_id,
                "filePath": c.file_path,
                "line": c.line,
                "status": c.status,
            }

        return {
            "mode": self.mode.value,
            "reviewer": None if self.reviewer is None else {
                "id": self.reviewer.user_id,
                "displayName": self.reviewer.display_name,
                "uniqueName": self.reviewer.unique_name,
                "descriptor": self.reviewer.descriptor,
            },
            "lastReviewAt": self.last_review_at,
            "lastReviewedCommit": self.last_reviewed_commit,
            "previousComments": [comment(c) for c in self.previous_comments],
            "activeComments": [comment(c) for c in self.active_comments],
            "resolvedComments": [comment(c) for c in self.resolved_comments],
            "previousFeedback": [
                {
                    "fingerprint": entry.fingerprint,
                    "threadStatus": entry.thread_status,
                    "lastAuthorReply": entry.last_author_reply,
                    "disposition": entry.disposition,
                    "threadId": entry.thread_id,
                }
                for entry in self.feedback
            ],
            "changedCommits": list(self.changed_commits),
            "changedFiles": list(self.changed_files),
            "reason": self.reason,
        }


def _parse_time(value: str) -> datetime | None:
    if not value:
        return None
    try:
        return datetime.fromisoformat(value.replace("Z", "+00:00"))
    except ValueError:
        return None


def _comment_from_dict(thread: dict[str, Any], raw: dict[str, Any]) -> ReviewComment:
    context = thread.get("threadContext") or {}
    author = raw.get("author") or {}
    if not isinstance(author, dict):
        author = {"displayName": author}
    return ReviewComment(
        comment_id=raw.get("id"),
        author_id=str(author.get("id") or raw.get("authorId") or ""),
        author_name=str(author.get("displayName") or raw.get("author") or "unknown"),
        text=str(raw.get("content") or raw.get("text") or ""),
        published_at=str(raw.get("publishedDate") or raw.get("publishedAt") or ""),
        commit_id=(raw.get("commitId") or raw.get("sourceCommit") or thread.get("commitId")),
        status=str(thread.get("status") or ""),
        thread_id=thread.get("id"),
        file_path=context.get("filePath"),
        line=((context.get("rightFileStart") or {}).get("line")),
    )


_DISMISSED_STATUSES = {"wontfix", "closed", "bydesign"}
_FIXED_STATUSES = {"fixed", "resolved"}
_TITLE_RE = re.compile(r"^####\s+[^—-]+(?:—|-)\s+(.+?)\s*$", re.MULTILINE)
_FEEDBACK_RE = re.compile(r"(?m)^<!--\s*prb-feedback:([a-zA-Z0-9]{6,32})\s*-->\s*$")


def _feedback_marker(thread: dict[str, Any]) -> str | None:
    for comment in thread.get("comments") or []:
        content = str(comment.get("content") or comment.get("text") or "")
        if match := _FEEDBACK_RE.search(content):
            return match.group(1)
    return None


def _thread_title(thread: dict[str, Any], comments: list[dict[str, Any]]) -> str:
    title = thread.get("title")
    if not title and comments:
        content = comments[0].get("content") or comments[0].get("text") or ""
        match = _TITLE_RE.search(str(content))
        title = match.group(1) if match else ""
    return str(title or "")
def _thread_fingerprint(thread: dict[str, Any]) -> str | None:
    context = thread.get("threadContext") or {}
    marker = _feedback_marker(thread)
    title = _thread_title(thread, thread.get("comments") or [])
    if marker:
        return marker
    if not title:
        return None
    return finding_fingerprint({"file": context.get("filePath"), "title": title})


def _comment_author_id(comment: dict[str, Any]) -> str:
    author = comment.get("author")
    if isinstance(author, dict):
        return str(author.get("id") or "")
    return str(comment.get("authorId") or "")


def _feedback_entry(thread: dict[str, Any], reviewer: ReviewerIdentity | None) -> FeedbackEntry | None:
    comments = _thread_comments(thread)
    if not any(reviewer and _comment_author_id(c) == reviewer.user_id for c in comments):
        return None
    fingerprint = _thread_fingerprint(thread)
    if not fingerprint:
        return None
    status = str(thread.get("status") or "").strip()
    status_key = status.casefold().replace(" ", "")
    disposition = "dismissed" if status_key in _DISMISSED_STATUSES else "fixed" if status_key in _FIXED_STATUSES else "unresolved"
    human = [c for c in comments if not reviewer or _comment_author_id(c) != reviewer.user_id]
    human.sort(key=lambda c: _parse_time(str(c.get("publishedDate") or c.get("publishedAt") or "")) or datetime.min.replace(tzinfo=timezone.utc))
    reply = str((human[-1].get("content") or human[-1].get("text") or "") if human else "")[:500]
    return FeedbackEntry(fingerprint, status, reply, disposition, thread.get("id"))
def _feedback_entries(
    threads: list[dict[str, Any]], reviewer: ReviewerIdentity | None
) -> tuple[FeedbackEntry, ...]:
    return tuple(
        entry
        for thread in threads or []
        if (entry := _feedback_entry(thread, reviewer)) is not None
    )


def _thread_comments(thread: dict[str, Any]) -> list[dict[str, Any]]:
    raw_comments = thread.get("comments")
    if isinstance(raw_comments, list):
        return [raw for raw in raw_comments if isinstance(raw, dict)]
    return [thread] if thread.get("authorId") or thread.get("author") else []


def normalize_comments(threads: list[dict[str, Any]]) -> tuple[ReviewComment, ...]:
    return tuple(
        _comment_from_dict(thread, raw)
        for thread in threads or []
        for raw in _thread_comments(thread)
    )


def _dated_commit(
    commit: dict[str, Any], reviewer_id: str | None, at: datetime | None
) -> tuple[datetime, str] | None:
    cid = commit.get("commitId") or commit.get("id")
    if not cid or reviewer_id and str(commit.get("authorId") or "") == reviewer_id:
        return None
    parsed = _parse_time(str(commit.get("authorDate") or commit.get("committerDate") or commit.get("date")))
    return (parsed, str(cid)) if parsed and at and parsed <= at else None


def _last_review_commit(
    latest: ReviewComment | None,
    reviewer: ReviewerIdentity | None,
    commits: list[dict[str, Any]],
) -> str | None:
    last_commit = latest.commit_id if latest else None
    if not latest or last_commit:
        return last_commit
    at = _parse_time(latest.published_at)
    reviewer_id = reviewer.user_id if reviewer else None
    dated = [
        candidate for commit in commits or []
        if (candidate := _dated_commit(commit, reviewer_id, at)) is not None
    ]
    return max(dated)[1] if dated else None


def _review_mode(
    reviewer: ReviewerIdentity | None,
    own: tuple[ReviewComment, ...],
    last_commit: str | None,
    current_commit: str | None,
    force_full: bool,
) -> tuple[ReviewMode, str]:
    if force_full:
        return ReviewMode.FORCE_FULL, "forced by configuration"
    if reviewer is None:
        return ReviewMode.FORCE_FULL, "authenticated reviewer identity unavailable"
    if not own:
        return ReviewMode.INITIAL, "no prior comments by authenticated reviewer"
    if not last_commit or not current_commit:
        return ReviewMode.FORCE_FULL, "reviewed commit boundary unavailable"
    if last_commit == current_commit:
        return ReviewMode.NO_OP, "no new commits since the previous review"
    return ReviewMode.FOLLOW_UP, "new commits since the previous review"


def select_review_state(
    *,
    reviewer: ReviewerIdentity | None,
    threads: list[dict[str, Any]],
    commits: list[dict[str, Any]],
    current_commit: str | None,
    force_full: bool = False,
    changed_commits: tuple[str, ...] = (),
    changed_files: tuple[str, ...] = (),
) -> ReviewState:
    comments = normalize_comments(threads)
    feedback = _feedback_entries(threads, reviewer)
    own = tuple(sorted(
        (c for c in comments if reviewer and c.author_id and c.author_id == reviewer.user_id),
        key=lambda c: _parse_time(c.published_at) or datetime.min.replace(tzinfo=timezone.utc),
    ))
    latest = own[-1] if own else None
    last_commit = _last_review_commit(latest, reviewer, commits)
    active = tuple(c for c in own if c.status.lower() not in {"closed", "resolved"})
    resolved = tuple(c for c in own if c.status.lower() in {"closed", "resolved"})
    mode, reason = _review_mode(reviewer, own, last_commit, current_commit, force_full)
    return ReviewState(
        reviewer=reviewer, mode=mode,
        last_review_at=latest.published_at if latest else None,
        last_reviewed_commit=last_commit, previous_comments=own,
        active_comments=active, resolved_comments=resolved, feedback=feedback,
        changed_commits=changed_commits, changed_files=changed_files, reason=reason,
    )



def filter_dismissed_findings(
    findings: list[dict[str, Any]], feedback: tuple[FeedbackEntry, ...]
) -> tuple[list[dict[str, Any]], list[dict[str, Any]]]:
    dismissed = {entry.fingerprint: entry for entry in feedback if entry.disposition == "dismissed"}
    kept: list[dict[str, Any]] = []
    discarded: list[dict[str, Any]] = []
    for finding in findings:
        entry = dismissed.get(finding_fingerprint(finding))
        if entry and not finding.get("regression", False):
            discarded.append({
                "reason": f"previously dismissed by author (thread {entry.thread_id})",
                "category": "previously-dismissed",
                "count": 1,
            })
        else:
            kept.append(finding)
    return kept, discarded

__all__ = [
    "FeedbackEntry",
    "filter_dismissed_findings",
    "ReviewComment",
    "ReviewMode",
    "ReviewState",
    "ReviewerIdentity",
    "normalize_comments",
    "select_review_state",
]
