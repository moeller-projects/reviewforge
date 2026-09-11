from types import SimpleNamespace
from unittest.mock import MagicMock

from reviewforge.pipeline.stage import Stage, StageContext, StageStatus, run_stages


class ConditionalStage(Stage):
    name = "conditional"

    def should_run(self, ctx):
        return False

    def run(self, ctx):  # pragma: no cover
        raise AssertionError("must not run")


def test_skipped_stage_exposes_reason_and_logs_it(monkeypatch):
    ctx = StageContext(
        cfg=MagicMock(),
        artifacts=MagicMock(),
        state=None,
        pi=MagicMock(),
        skip_reason="No new commits since the previous review.\n\nSkipping review.",
    )
    messages = []
    monkeypatch.setattr("reviewforge.pipeline.stage.log_info", messages.append)

    results = run_stages([ConditionalStage()], ctx)

    assert results[0].status == StageStatus.SKIPPED
    assert results[0].reason == "No new commits since the previous review."
    assert any(
        "stage conditional skipped: No new commits since the previous review." in message
        for message in messages
    )


def test_generic_skip_has_a_reason_when_no_review_context_exists():
    ctx = StageContext(cfg=MagicMock(), artifacts=MagicMock(), state=None, pi=MagicMock())

    result = ConditionalStage()(ctx)

    assert result.status == StageStatus.SKIPPED
    assert result.reason == "conditional precondition was not satisfied"


def test_run_summary_stage_details_keep_skip_reason():
    from reviewforge.pipeline.orchestrator import _record_results
    from reviewforge.pipeline.stage import StageResult

    summary = MagicMock()
    result = StageResult(
        name="detect_review_mode",
        status=StageStatus.SKIPPED,
        started_at="start",
        finished_at="finish",
        duration_ms=1,
        reason="review mode is no_op",
    )

    _record_results(summary, [result])

    record = summary.add_stage.call_args.args[0]
    assert record.details["reason"] == "review mode is no_op"
