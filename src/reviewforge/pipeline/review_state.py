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


def _thread_fingerprint(thread: dict[str, Any]) -> str | None:
    context = thread.get("threadContext") or {}
    comments = thread.get("comments") or []
    if comments:
        for comment in comments:
            content = str(comment.get("content") or comment.get("text") or "")
            if match := _FEEDBACK_RE.search(content):
                return match.group(1)
    title = thread.get("title")
    if not title and comments:
        content = comments[0].get("content") or comments[0].get("text") or ""
        match = _TITLE_RE.search(str(content))
        title = match.group(1) if match else ""
    if not title:
        return None
    return finding_fingerprint({"file": context.get("filePath"), "title": title})


def _comment_author_id(comment: dict[str, Any]) -> str:
    author = comment.get("author")
    if isinstance(author, dict):
        return str(author.get("id") or "")
    return str(comment.get("authorId") or "")


def _feedback_entries(
    threads: list[dict[str, Any]], reviewer: ReviewerIdentity | None
) -> tuple[FeedbackEntry, ...]:
    entries: list[FeedbackEntry] = []
    for thread in threads or []:
        comments = [c for c in thread.get("comments") or [] if isinstance(c, dict)]
        bot_comments = [
            c for c in comments
            if reviewer and _comment_author_id(c) == reviewer.user_id
        ]
        if not bot_comments:
            continue
        fingerprint = _thread_fingerprint(thread)
        if not fingerprint:
            continue
        status = str(thread.get("status") or "").strip()
        status_key = status.casefold().replace(" ", "")
        disposition = (
            "dismissed" if status_key in _DISMISSED_STATUSES
            else "fixed" if status_key in _FIXED_STATUSES
            else "unresolved"
        )
        human = [
            c for c in comments
            if _comment_author_id(c) != reviewer.user_id
        ]
        human.sort(
            key=lambda c: _parse_time(
                str(c.get("publishedDate") or c.get("publishedAt") or "")
            ) or datetime.min.replace(tzinfo=timezone.utc)
        )
        reply = str(
            (human[-1].get("content") or human[-1].get("text") or "")
            if human else ""
        )[:500]
        entries.append(
            FeedbackEntry(fingerprint, status, reply, disposition, thread.get("id"))
        )
    return tuple(entries)


def normalize_comments(threads: list[dict[str, Any]]) -> tuple[ReviewComment, ...]:
    comments: list[ReviewComment] = []
    for thread in threads or []:
        raw_comments = thread.get("comments")
        if not isinstance(raw_comments, list):
            raw_comments = [thread] if thread.get("authorId") or thread.get("author") else []
        comments.extend(_comment_from_dict(thread, raw) for raw in raw_comments if isinstance(raw, dict))
    return tuple(comments)


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
    own = tuple(c for c in comments if reviewer and c.author_id and c.author_id == reviewer.user_id)
    own = tuple(sorted(
        own,
        key=lambda c: _parse_time(c.published_at) or datetime.min.replace(tzinfo=timezone.utc),
    ))
    latest = own[-1] if own else None
    last_commit = latest.commit_id if latest else None
    if latest and not last_commit:
        at = _parse_time(latest.published_at)
        dated = []
        for commit in commits or []:
            cid = commit.get("commitId") or commit.get("id")
            stamp = commit.get("authorDate") or commit.get("committerDate") or commit.get("date")
            parsed = _parse_time(str(stamp or ""))
            if cid and at and parsed and parsed <= at:
                dated.append((parsed, str(cid)))
        if dated:
            last_commit = max(dated)[1]
    active = tuple(c for c in own if c.status.lower() not in {"closed", "resolved"})
    resolved = tuple(c for c in own if c.status.lower() in {"closed", "resolved"})
    if force_full:
        mode, reason = ReviewMode.FORCE_FULL, "forced by configuration"
    elif reviewer is None:
        mode, reason = ReviewMode.FORCE_FULL, "authenticated reviewer identity unavailable"
    elif not own:
        mode, reason = ReviewMode.INITIAL, "no prior comments by authenticated reviewer"
    elif not last_commit or not current_commit:
        mode, reason = ReviewMode.FORCE_FULL, "reviewed commit boundary unavailable"
    elif last_commit == current_commit:
        mode, reason = ReviewMode.NO_OP, "no new commits since the previous review"
    else:
        mode, reason = ReviewMode.FOLLOW_UP, "new commits since the previous review"
    return ReviewState(
        reviewer=reviewer,
        mode=mode,
        last_review_at=latest.published_at if latest else None,
        last_reviewed_commit=last_commit,
        previous_comments=own,
        active_comments=active,
        resolved_comments=resolved,
        feedback=feedback,
        changed_commits=changed_commits,
        changed_files=changed_files,
        reason=reason,
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
    "build_review_state_payload",
    "normalize_comments",
    "select_review_state",
]
