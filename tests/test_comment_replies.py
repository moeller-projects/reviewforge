"""Tests for the comment-reply feature: detection, schema, stage, and CLI."""
from __future__ import annotations

import json
from pathlib import Path
from unittest.mock import MagicMock, patch

import pytest

from reviewforge.ado import posting
from reviewforge.artifacts import manager
from reviewforge.config import Config
from reviewforge.exceptions import AdoApiError
from reviewforge.pipeline.schemas import CommentReplies
from reviewforge.pipeline.stage import StageContext
from reviewforge.pipeline.stages.reply_to_comments import ReplyToCommentsStage

BOT = {"id": "bot-guid", "displayName": "Review Bot"}
HUMAN = {"id": "human-guid", "displayName": "Alice"}
MARKER = "abc123def456"


def _comment(author, text):
    return {"author": author, "content": text}


def _bot_comment(text="finding body"):
    return _comment(BOT, f"{text}\n<!-- prb:{MARKER} -->\n")


def _thread(thread_id, comments, status="active"):
    return {"id": thread_id, "status": status, "comments": comments}


# ---------------------------------------------------------------------------
# find_awaiting_replies — pure logic
# ---------------------------------------------------------------------------


class TestFindAwaitingReplies:
    def test_human_last_is_awaiting(self):
        threads = [_thread(1, [_bot_comment(), _comment(HUMAN, "I disagree")])]
        assert [t["id"] for t in posting.find_awaiting_replies(threads)] == [1]

    def test_bot_last_is_not_awaiting(self):
        # The bot's own markerless follow-up (reply or stale note) counts as
        # bot-authored via the marker comment's author identity.
        threads = [
            _thread(1, [_bot_comment(), _comment(HUMAN, "q?"), _comment(BOT, "answer")])
        ]
        assert posting.find_awaiting_replies(threads) == []

    def test_human_after_bot_followup_is_awaiting(self):
        threads = [
            _thread(1, [_bot_comment(), _comment(BOT, "stale note"), _comment(HUMAN, "still wrong")])
        ]
        assert [t["id"] for t in posting.find_awaiting_replies(threads)] == [1]

    @pytest.mark.parametrize("status", ["fixed", "wontFix", "wontfix", "byDesign", "closed"])
    def test_terminal_thread_status_is_skipped(self, status):
        threads = [_thread(1, [_bot_comment(), _comment(HUMAN, "disagree")], status=status)]
        assert posting.find_awaiting_replies(threads) == []

    def test_unmarked_thread_is_skipped(self):
        threads = [_thread(1, [_comment(HUMAN, "hello"), _comment(HUMAN, "anyone?")])]
        assert posting.find_awaiting_replies(threads) == []

    def test_author_falls_back_to_display_name(self):
        bot = {"displayName": "Review Bot"}
        human = {"displayName": "Alice"}
        threads = [
            _thread(1, [_comment(bot, f"body\nprb:{MARKER}"), _comment(bot, "follow-up")]),
            _thread(2, [_comment(bot, f"body\nprb:{MARKER}"), _comment(human, "nope")]),
        ]
        assert [t["id"] for t in posting.find_awaiting_replies(threads)] == [2]

    def test_empty_inputs(self):
        assert posting.find_awaiting_replies([]) == []
        assert posting.find_awaiting_replies(None) == []
        assert posting.find_awaiting_replies([{"id": 1, "status": "active"}]) == []


# ---------------------------------------------------------------------------
# CommentReplies schema
# ---------------------------------------------------------------------------


class TestCommentRepliesSchema:
    def test_valid_payload(self):
        result = CommentReplies.model_validate(
            {"replies": [{"thread_id": 7, "reply": "you are right"}]}
        )
        assert result.replies[0].thread_id == 7

    def test_extra_keys_ignored(self):
        result = CommentReplies.model_validate(
            {"replies": [{"thread_id": 1, "reply": "x", "extra": 1}], "other": True}
        )
        assert len(result.replies) == 1

    def test_invalid_payload_rejected(self):
        with pytest.raises(Exception):
            CommentReplies.model_validate({"replies": [{"thread_id": 1}]})


# ---------------------------------------------------------------------------
# ReplyToCommentsStage
# ---------------------------------------------------------------------------


def _cfg(tmp_path: Path, *, dry_run: bool, reply_comments: bool = True) -> Config:
    prompt = tmp_path / "comment-reply.md"
    prompt.write_text("reply prompt", encoding="utf-8")
    return Config(
        ado_org="contoso",
        ado_project="Payments",
        ado_repo_id="api",
        pr_id="42",
        ado_token="tok",
        source_branch="feature",
        target_branch="main",
        workspace=tmp_path / "workspace",
        clone_root=tmp_path / "workspace",
        review_language="English",
        review_prompt_path=prompt,
        intent_prompt_path=prompt,
        context_plan_prompt_path=prompt,
        context_digest_prompt_path=prompt,
        verify_prompt_path=prompt,
        severity_prompt_path=prompt,
        standards_path=prompt,
        pi_model="test/model",
        max_diff_bytes=100,
        chunk_trigger_diff_bytes=100,
        disable_chunk_review=False,
        pi_timeout_secs=5,
        dry_run=dry_run,
        include_work_items=True,
        include_existing_comments=True,
        verify_findings=True,
        force_review=False,
        review_target_branches="",
        review_artifact_dir=None,
        review_artifact_root=tmp_path / "artifacts",
        review_run_id="run-1",
        reply_comments=reply_comments,
        comment_reply_prompt_path=prompt,
    )


def _ctx(cfg: Config, pi_payload: dict, *, explicit_reply_command: bool = False) -> StageContext:
    artifacts = manager.create(cfg)
    pi = MagicMock()
    pi.run_json.side_effect = lambda prompt, stdin, out, stage: out.write_text(
        json.dumps(pi_payload), encoding="utf-8"
    )
    ctx = StageContext(cfg=cfg, artifacts=artifacts, state=None, pi=pi)
    ctx.extras["explicit_reply_command"] = explicit_reply_command
    return ctx
def _pending_thread(thread_id: int = 9):
    return {
        "id": thread_id,
        "status": "active",
        "threadContext": {"filePath": "/src/app.py", "rightFileStart": {"line": 4}},
        "comments": [_bot_comment(), _comment(HUMAN, "this is intentional")],
    }


class TestReplyToCommentsStage:
    def test_disabled_skips(self, tmp_path):
        stage = ReplyToCommentsStage()
        ctx = _ctx(_cfg(tmp_path, dry_run=False, reply_comments=False), {"replies": []})
        assert stage.should_run(ctx) is False

    def test_no_pending_writes_empty_artifact(self, tmp_path):
        ctx = _ctx(_cfg(tmp_path, dry_run=False), {"replies": []})
        with patch(
            "reviewforge.pipeline.stages.reply_to_comments.AdoClient"
        ) as client_cls:
            client_cls.return_value.get_threads.return_value = []
            result = ReplyToCommentsStage().run(ctx)
        assert result == {"awaiting": 0, "replied": 0}
        assert json.loads(ctx.artifacts.comment_replies.read_text()) == {"replies": []}
        ctx.pi.run_json.assert_not_called()

    def test_posts_valid_replies_and_drops_invalid(self, tmp_path):
        payload = {
            "replies": [
                {"thread_id": 9, "reply": "Fair point — resolving."},
                {"thread_id": 9, "reply": "duplicate should be dropped"},
                {"thread_id": 999, "reply": "unknown thread"},
                {"thread_id": 9, "reply": "   "},
            ]
        }
        ctx = _ctx(_cfg(tmp_path, dry_run=False), payload)
        with patch(
            "reviewforge.pipeline.stages.reply_to_comments.AdoClient"
        ) as client_cls:
            client = client_cls.return_value
            client.get_threads.return_value = [_pending_thread()]
            result = ReplyToCommentsStage().run(ctx)
        assert result == {"awaiting": 1, "replied": 1}
        client.add_comment.assert_called_once_with("42", 9, "Fair point — resolving.")
        recorded = json.loads(ctx.artifacts.comment_replies.read_text())["replies"]
        assert recorded == [
            {"thread_id": 9, "reply": "Fair point — resolving.", "posted": True}
        ]

    def test_dry_run_prints_without_posting(self, tmp_path, capsys):
        payload = {"replies": [{"thread_id": 9, "reply": "draft reply"}]}
        ctx = _ctx(_cfg(tmp_path, dry_run=True), payload, explicit_reply_command=True)
        with patch(
            "reviewforge.pipeline.stages.reply_to_comments.AdoClient"
        ) as client_cls:
            client = client_cls.return_value
            client.get_threads.return_value = [_pending_thread()]
            result = ReplyToCommentsStage().run(ctx)
        assert result == {"awaiting": 1, "replied": 0}
        client.add_comment.assert_not_called()
        assert "draft reply" in capsys.readouterr().out
        assert json.loads(ctx.artifacts.comment_replies.read_text())["replies"] == [
            {"thread_id": 9, "reply": "draft reply", "posted": False}
        ]

    def test_automatic_dry_run_records_without_printing(self, tmp_path, capsys):
        payload = {"replies": [{"thread_id": 9, "reply": "draft reply"}]}
        ctx = _ctx(_cfg(tmp_path, dry_run=True), payload)
        with patch(
            "reviewforge.pipeline.stages.reply_to_comments.AdoClient"
        ) as client_cls:
            client = client_cls.return_value
            client.get_threads.return_value = [_pending_thread()]
            ReplyToCommentsStage().run(ctx)
        assert capsys.readouterr().out == ""
        assert json.loads(ctx.artifacts.comment_replies.read_text())["replies"][0]["posted"] is False

    def test_failed_reply_is_recorded_and_later_replies_continue(self, tmp_path):
        payload = {
            "replies": [
                {"thread_id": 9, "reply": "first"},
                {"thread_id": 10, "reply": "second"},
            ]
        }
        ctx = _ctx(_cfg(tmp_path, dry_run=False), payload)
        with patch(
            "reviewforge.pipeline.stages.reply_to_comments.AdoClient"
        ) as client_cls:
            client = client_cls.return_value
            client.get_threads.return_value = [_pending_thread(9), _pending_thread(10)]
            client.add_comment.side_effect = [AdoApiError("temporary failure"), None]
            result = ReplyToCommentsStage().run(ctx)
        assert result == {"awaiting": 2, "replied": 1}
        assert client.add_comment.call_count == 2
        assert json.loads(ctx.artifacts.comment_replies.read_text())["replies"] == [
            {"thread_id": 9, "reply": "first", "posted": False, "error": "temporary failure"},
            {"thread_id": 10, "reply": "second", "posted": True},
        ]


    @pytest.mark.parametrize(
        ("model_output", "error_type"),
        [
            ("not valid JSON", "JSONDecodeError"),
            ('{"replies": [{"thread_id": 9}]}', "ValidationError"),
        ],
    )
    def test_malformed_model_output_fails_stage_without_posting(
        self, tmp_path, model_output, error_type
    ):
        ctx = _ctx(_cfg(tmp_path, dry_run=False), {"replies": []})
        ctx.pi.run_json.side_effect = lambda _prompt, _stdin, output, _stage: output.write_text(
            model_output, encoding="utf-8"
        )
        with patch(
            "reviewforge.pipeline.stages.reply_to_comments.AdoClient"
        ) as client_cls:
            client = client_cls.return_value
            client.get_threads.return_value = [_pending_thread()]
            result = ReplyToCommentsStage()(ctx)

        assert result.status == "failed"
        assert result.error.startswith(f"{error_type}:")
        client.add_comment.assert_not_called()

# ---------------------------------------------------------------------------
# CLI wiring
# ---------------------------------------------------------------------------


class TestCli:
    def test_reply_subcommand_registered(self):
        from reviewforge.cli import build_parser

        args = build_parser().parse_args(["reply", "--pr", "42"])
        assert args._command == "reply"
        assert args.pr_id == "42"

    def test_no_reply_flag(self):
        from reviewforge.cli import build_parser

        args = build_parser().parse_args(["review", "--no-reply"])
        assert args.reply_comments is False

    def test_reply_comments_config_default_and_override(self):
        env = {
            "ADO_AUTH_TOKEN": "tok",
            "ADO_ORG": "o",
            "ADO_PROJECT": "p",
            "ADO_REPO_ID": "r",
            "PR_ID": "1",
        }
        assert Config.from_sources({}, env=env).reply_comments is True
        assert Config.from_sources({}, env={**env, "REPLY_COMMENTS": "0"}).reply_comments is False
        assert Config.from_sources({"reply_comments": False}, env=env).reply_comments is False
