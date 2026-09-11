"""Stage: answer unanswered human replies on bot comment threads.

Detects bot-marked threads whose last comment is human-authored, drafts a
reply per thread with the configured model runner (reusing the Pi session so
the model retains its review context), validates the output, and appends each
reply to its existing thread. Under ``dry_run`` the replies are printed and
recorded but never posted.
"""
from __future__ import annotations

import json
from typing import Any

from ...ado.client import AdoClient
from ...ado.posting import find_awaiting_replies
from ...artifacts.builder import write_json
from ...exceptions import ReviewForgeError
from ...runlog import info as _log
from ..schemas import CommentReplies, load_and_validate
from ..stage import Stage, StageContext


def _build_client(cfg: Any) -> AdoClient:
    return AdoClient(
        cfg.ado_org,
        cfg.ado_project,
        cfg.ado_repo_id,
        token=cfg.ado_token,
        retry_attempts=cfg.ado_retry_attempts,
        retry_base_delay=cfg.ado_retry_base_delay,
        retry_cap_delay=cfg.ado_retry_cap_delay,
        retry_budget_secs=cfg.ado_retry_budget_secs,
    )


def _thread_payload(thread: dict[str, Any]) -> dict[str, Any]:
    ctx = thread.get("threadContext") or {}
    return {
        "thread_id": thread.get("id"),
        "file": ctx.get("filePath"),
        "line": (ctx.get("rightFileStart") or {}).get("line"),
        "status": thread.get("status") or "",
        "comments": [
            {
                "author": (c.get("author") or {}).get("displayName")
                if isinstance(c.get("author"), dict)
                else str(c.get("author") or ""),
                "content": c.get("content") or "",
            }
            for c in thread.get("comments") or []
        ],
    }


def _valid_replies(result: CommentReplies, pending: list[dict[str, Any]]) -> list[dict[str, Any]]:
    """Keep one non-empty reply for each currently pending thread."""
    known_ids = {thread.get("id") for thread in pending}
    replies: list[dict[str, Any]] = []
    seen_ids: set[int] = set()
    for item in result.replies:
        body = item.reply.strip()
        if item.thread_id not in known_ids or not body or item.thread_id in seen_ids:
            continue
        seen_ids.add(item.thread_id)
        replies.append(
            {"thread_id": item.thread_id, "reply": body, "posted": False}
        )
    dropped = len(result.replies) - len(replies)
    if dropped:
        _log(f"dropped {dropped} invalid reply/replies (unknown thread or empty body)")
    return replies


def _generate_replies(
    ctx: StageContext, pending: list[dict[str, Any]]
) -> list[dict[str, Any]]:
    """Generate and validate replies for the pending threads."""
    payload = json.dumps(
        [_thread_payload(thread) for thread in pending],
        ensure_ascii=False,
        indent=2,
    )
    raw_path = ctx.artifacts.raw_dir / "comment-replies.json"
    ctx.pi.run_json(
        ctx.cfg.comment_reply_prompt_path,
        payload,
        raw_path,
        "comment reply",
    )
    ctx.last_token_usage = ctx.pi.last_tokens
    return _valid_replies(load_and_validate(raw_path, CommentReplies), pending)


def _post_replies(
    cfg: Any, client: AdoClient, replies: list[dict[str, Any]]
) -> tuple[int, int]:
    """Post replies independently, returning ``(posted, failed)`` counts."""
    posted = 0
    failed = 0
    for entry in replies:
        try:
            client.add_comment(cfg.pr_id, entry["thread_id"], entry["reply"])
        except ReviewForgeError as exc:
            failed += 1
            entry["error"] = str(exc)
            _log(f"reply to thread {entry['thread_id']} failed: {exc}")
        else:
            entry["posted"] = True
            posted += 1
    return posted, failed


def _handle_dry_run(ctx: StageContext, replies: list[dict[str, Any]]) -> None:
    """Record dry-run intent and print only for the explicit reply command."""
    _log("DRY_RUN=1; recording replies without posting")
    if ctx.extras.get("explicit_reply_command"):
        print(json.dumps({"replies": replies}, ensure_ascii=False, indent=2))


class ReplyToCommentsStage(Stage):
    """Generate and post replies to unanswered human comments on bot threads."""

    name = "reply_to_comments"

    def should_run(self, ctx: StageContext) -> bool:
        return bool(ctx.cfg.reply_comments)

    def run(self, ctx: StageContext) -> dict[str, Any]:
        cfg = ctx.cfg
        client = _build_client(cfg)
        pending = find_awaiting_replies(client.get_threads(cfg.pr_id))
        if not pending:
            write_json(ctx.artifacts.comment_replies, {"replies": []})
            return {"awaiting": 0, "replied": 0}

        _log(f"{len(pending)} bot thread(s) awaiting a reply")
        ctx.pi.set_working_dir(ctx.state.repo_dir if ctx.state else None)
        replies = _generate_replies(ctx, pending)
        posted = 0
        failed = 0
        if cfg.dry_run:
            _handle_dry_run(ctx, replies)
        else:
            posted, failed = _post_replies(cfg, client, replies)
            _log(f"posted {posted} reply/replies on PR #{cfg.pr_id}")
        write_json(ctx.artifacts.comment_replies, {"replies": replies})
        if failed:
            _log(f"{failed} reply/replies failed after ADO retries")
        return {"awaiting": len(pending), "replied": posted}


__all__ = ["ReplyToCommentsStage"]
