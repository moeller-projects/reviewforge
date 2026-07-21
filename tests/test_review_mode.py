from __future__ import annotations

from reviewforge.pipeline.review_state import (
    ReviewMode,
    ReviewerIdentity,
    select_review_state,
)


REVIEWER = ReviewerIdentity("u1", "Reviewer")


def thread(author_id: str, commit_id: str | None, *, status: str = "active", when: str = "2026-07-19T10:00:00Z"):
    return {
        "id": 1,
        "status": status,
        "commitId": commit_id,
        "comments": [{
            "id": 2,
            "author": {"id": author_id, "displayName": "Reviewer"},
            "content": "finding",
            "publishedDate": when,
        }],
    }


def test_initial_review_when_reviewer_has_no_comments():
    state = select_review_state(
        reviewer=REVIEWER, threads=[], commits=[], current_commit="head"
    )
    assert state.mode is ReviewMode.INITIAL


def test_follow_up_keeps_previous_comments_and_selects_new_commit():
    state = select_review_state(
        reviewer=REVIEWER,
        threads=[thread("u1", "old")],
        commits=[],
        current_commit="new",
    )
    assert state.mode is ReviewMode.FOLLOW_UP
    assert state.last_reviewed_commit == "old"
    assert len(state.active_comments) == 1


def test_no_op_when_head_is_last_reviewed_commit():
    state = select_review_state(
        reviewer=REVIEWER,
        threads=[thread("u1", "head")],
        commits=[],
        current_commit="head",
    )
    assert state.mode is ReviewMode.NO_OP
    assert "Skipping" not in state.reason


def test_force_full_wins_over_history():
    state = select_review_state(
        reviewer=REVIEWER,
        threads=[thread("u1", "head")],
        commits=[],
        current_commit="head",
        force_full=True,
    )
    assert state.mode is ReviewMode.FORCE_FULL


def test_missing_boundary_falls_back_to_full_review():
    state = select_review_state(
        reviewer=REVIEWER,
        threads=[thread("u1", None)],
        commits=[],
        current_commit="head",
    )
    assert state.mode is ReviewMode.FORCE_FULL


def test_other_reviewers_do_not_count():
    state = select_review_state(
        reviewer=REVIEWER,
        threads=[thread("other", "head")],
        commits=[],
        current_commit="head",
    )
    assert state.mode is ReviewMode.INITIAL


def test_cli_exposes_force_full_review():
    from reviewforge.cli import build_parser

    args = build_parser().parse_args(["review", "--force-full-review"])
    assert args.force_full_review is True


def test_no_op_stage_skips_reasoning_without_pi(tmp_path):
    from types import SimpleNamespace

    from reviewforge.pipeline.stage import StageContext, StageStatus
    from reviewforge.pipeline.stages.detect_review_mode import DetectReviewModeStage
    from reviewforge.pipeline.stages.execute_reasoning_engine import ExecuteReasoningEngineStage

    final = tmp_path / "final-findings.json"
    ctx = StageContext(
        cfg=SimpleNamespace(force_full_review=False),
        artifacts=SimpleNamespace(final=final),
        state=None,
        pi=SimpleNamespace(),
        metadata={
            "sourceCommit": "head",
            "reviewState": {
                "reviewer": {"id": "u1"},
                "threads": [thread("u1", "head")],
                "commits": [],
                "currentCommit": "head",
            },
        },
    )
    assert DetectReviewModeStage()(ctx).status == StageStatus.OK
    result = ExecuteReasoningEngineStage()(ctx)
    assert result.status == StageStatus.SKIPPED
    assert "Skipping review." in final.read_text()


def test_timestamp_infers_last_commit_and_classifies_resolved_comment():
    state = select_review_state(
        reviewer=REVIEWER,
        threads=[thread("u1", None, status="resolved")],
        commits=[{"commitId": "old", "authorDate": "2026-07-19T09:00:00Z"}],
        current_commit="new",
    )
    assert state.mode is ReviewMode.FOLLOW_UP
    assert state.last_reviewed_commit == "old"
    assert len(state.resolved_comments) == 1
    assert not state.active_comments


def test_unparseable_comment_time_falls_back_safely():
    state = select_review_state(
        reviewer=REVIEWER,
        threads=[thread("u1", "old", when="not-a-date")],
        commits=[],
        current_commit="new",
    )
    assert state.mode is ReviewMode.FOLLOW_UP
