from types import SimpleNamespace

from reviewforge.reasoning import single_pi


def test_context_caps_preserve_pointers_and_utf8_boundaries():
    assert single_pi._byte_cap_with_pointer("éé", 3, None) == "é"
    pointed = single_pi._byte_cap_with_pointer("abcdef" * 20, 80, "context.json")
    assert "full data: context.json" in pointed
    assert single_pi._byte_cap_with_pointer("abcdef", 0, None) == ""
    assert single_pi.render_section("Items", [1, 2, 3], 2, "items.json").endswith("items.json")


def test_review_state_and_context_sections_bound_large_inputs():
    context = {key: list(range(30)) for key in ("previousComments", "changedFiles")}
    trimmed = single_pi._trim_review_state(context, object())
    assert len(trimmed["previousComments"]) == single_pi._CONTEXT_MAX_REVIEW_ITEMS
    pointers = single_pi._review_state_pointers(context, object())
    assert len(pointers) == 2
    assert single_pi._feedback_section([], None) == []
    assert single_pi._feedback_section(list(range(20)), object())
    assert single_pi._staging_section({"a": {"description": "summary"}}, object())


def test_runner_and_graph_helpers_handle_fallback_shapes():
    assert single_pi._runner_usage(SimpleNamespace(token_usage={"in": 2})) == {"in": 2, "out": 0, "total": 0}
    assert single_pi._runner_usage(SimpleNamespace(last_tokens={"out": 3})) == {"in": 0, "out": 3, "total": 0}
    assert single_pi._runner_usage(SimpleNamespace()) == {"in": 0, "out": 0, "total": 0}
    assert single_pi._runner_count(SimpleNamespace(invocation_count="1"), "invocation_count") == 0
    assert single_pi._graph_architecture_present(SimpleNamespace(extras={"graph_context": {"architecture": {"status": "ok"}}}))
    assert not single_pi._graph_architecture_present(SimpleNamespace(extras={"graph_context": None}))


def test_diff_partition_and_validation_error_rendering():
    diff = "diff --git a/a.py b/a.py\n+one\ndiff --git a/b.py b/b.py\n+two\n"
    assert len(single_pi._diff_chunks(diff, 30)) == 2
    assert single_pi._diff_chunks(diff, 10_000) == [diff]
    assert single_pi._format_validation_error(ValueError("bad")) == "bad"


def test_single_pi_context_and_escalation_edge_paths(tmp_path, monkeypatch):
    from dataclasses import replace
    from test_reasoning import _cfg, _stage_context, _valid_review_result_payload
    from reviewforge.pipeline.schemas import ReviewResult

    cfg = _cfg(tmp_path)
    ctx = _stage_context(cfg, SimpleNamespace())
    ctx.artifacts.commits.write_text("abc commit\n", encoding="utf-8")
    assert single_pi._all_commit_lines(ctx) == ["abc commit"]
    assert single_pi._trim_review_state([], None) == []
    assert single_pi._uncertainty_key(SimpleNamespace(topic=" Topic ", reason=" Reason ")) == ("topic", "reason")
    assert single_pi._discarded_key(SimpleNamespace(category=" Cat ", reason=" Reason ")) == ("cat", "reason")

    ctx.extras["graph_context"] = {"architecture": {"status": "ok"}}
    ctx.cfg = replace(cfg, graph_arch=True)
    assert single_pi._graph_architecture_present(ctx)
    instruction = single_pi._escalation_instruction(
        [SimpleNamespace(files=["b.py", "a.py"], danger="high", suggested_focus="security", reason="risk")],
        "diff",
    )
    assert "a.py" in instruction and "security" in instruction

    base = ReviewResult.model_validate(_valid_review_result_payload())
    assert single_pi._merge_escalation(base, base).findings
    assert single_pi._execute_escalation(
        SimpleNamespace(run_json=lambda *args: (_ for _ in ()).throw(RuntimeError("boom"))),
        cfg,
        "instruction",
        tmp_path / "escalation.json",
    ) is None
    replacement = SimpleNamespace(set_working_dir=lambda _path: None)
    ctx.pi.session_id = "session"
    monkeypatch.setattr("reviewforge.ai.model_runner.create_model_runner", lambda _cfg: replacement)
    runner = single_pi._escalation_runner(ctx, replace(cfg, escalation_model="other/model"))
    assert runner is replacement

    from reviewforge.pipeline.schemas import CoverageGap, EscalationHint
    hint = EscalationHint(files=["a.py"], reason="risk", suggested_focus="security-audit", danger="high")
    gap = CoverageGap(behavior="behavior", suggested_test="test", file="a.py")
    enriched = base.model_copy(update={"test_gaps": [gap], "escalation_hints": [hint]})
    assert single_pi._merge_escalation(enriched, enriched).test_gaps
    ctx.cfg = replace(cfg, escalation_review_enabled=True, escalation_model="other/model")
    ctx.pi.run_json = lambda *args: None
    monkeypatch.setattr(single_pi, "_execute_escalation", lambda *args: base)
    assert single_pi._run_escalation_pass(single_pi, ctx, enriched) is base


def test_single_pi_graph_state_and_worker_failure_paths(tmp_path, monkeypatch):
    import pytest
    from unittest.mock import MagicMock
    from test_reasoning import _cfg, _stage_context
    from reviewforge.artifacts import builder
    from reviewforge.git.chunker import DiffChunk
    from reviewforge.pipeline.schemas import ReviewResult, Uncertainty
    from reviewforge.exceptions import SchemaValidationError

    cfg = _cfg(tmp_path)
    ctx = _stage_context(cfg, MagicMock())
    ctx.extras["graph_context"] = {"architecture": {"status": "ok"}, "api_surface": {"status": "ok"}}
    ctx.cfg = cfg.with_overrides(graph_arch=True, graph_api_diff=True)
    assert single_pi._prefix_graph_context(ctx, object())
    ctx.state.files = []
    ctx.files_text = "\n".join(f"file-{index}.py" for index in range(60))
    assert "full data" in single_pi._changed_files_section(ctx, object())
    monkeypatch.setattr(single_pi.git_ops, "run_git", lambda *args: "abc commit")
    ctx.artifacts.commits.unlink(missing_ok=True)
    ctx.state.repo_dir = tmp_path
    ctx.state.range_spec = "HEAD"
    assert single_pi._all_commit_lines(ctx) == ["abc commit"]

    ctx.pi.run_json.side_effect = lambda _p, _s, out, _stage: builder.write_json(out, {"findings": "bad"})
    with pytest.raises(SchemaValidationError):
        single_pi._review_scope(ctx, cfg, DiffChunk("diff", "a.py\n", scope_id="scope-01"), 1, 1)

    result = ReviewResult.model_validate(
        __import__("test_reasoning")._valid_review_result_payload()
    )
    uncertain = result.model_copy(
        update={"uncertainties": [Uncertainty(topic="cross-chunk:flow", reason="a.py", confidence="low")]}
    )
    assert single_pi._derive_review_confidence(uncertain)[0] == "medium"


def test_schema_rejection_paths_are_exercised():
    import pytest
    from test_reasoning import _valid_review_result_payload
    from reviewforge.pipeline.schemas import (
        CoverageGap,
        EscalationHint,
        PrSummary,
        RichEvidence,
        RichFinding,
        ReviewResult,
        Uncertainty,
    )

    with pytest.raises(ValueError):
        PrSummary(positive_observations=["a", "b", "c", "d"])
    with pytest.raises(ValueError):
        RichEvidence(changedLines=[0])
    with pytest.raises(ValueError):
        RichEvidence(changedLines=[1], classification="unknown")
    with pytest.raises(ValueError):
        CoverageGap(behavior="", suggested_test="test", file="a.py")
    with pytest.raises(ValueError):
        CoverageGap(behavior="behavior", suggested_test="test", file="../a.py")
    with pytest.raises(ValueError):
        Uncertainty(topic="x", reason="")
    with pytest.raises(ValueError):
        Uncertainty(topic="cross-chunk:x", reason="a.py", confidence="high")
    with pytest.raises(ValueError):
        EscalationHint(files=["a.py"], reason="", suggested_focus="security-audit", danger="high")
    with pytest.raises(ValueError):
        RichFinding(
            title="x", observation="o", impact="i", recommendation="r",
            severity="minor", file="../a.py",
        )
    with pytest.raises(ValueError):
        ReviewResult.model_validate({"pr_summary": {"intent": "x"}})
    with pytest.raises(ValueError):
        ReviewResult.model_validate({**_valid_review_result_payload(), "review_summary": None})


def test_additional_schema_limits_and_work_item_rules():
    import pytest
    from test_reasoning import _valid_review_result_payload
    from reviewforge.pipeline.schemas import (
        ChunkSynthesis,
        CoverageGap,
        GoodPractice,
        RichEvidence,
        RichFinding,
        ReviewResult,
    )

    with pytest.raises(ValueError):
        RichFinding(title="x", observation="o", impact="i", recommendation="r", severity="minor", file="")
    with pytest.raises(ValueError):
        RichFinding(title="x", observation="o", impact="i", recommendation="r", severity="minor", file="C:/x")
    evidence = RichEvidence(changedLines=[1], whyNewInThisPr="new")
    with pytest.raises(ValueError):
        RichFinding(
            title="Work item #1", observation="o", impact="i", recommendation="r",
            severity="minor", evidence=evidence,
        )
    with pytest.raises(ValueError):
        RichFinding(
            title="Work item #1", observation="o", impact="i", recommendation="r",
            severity="major", file="a.py", evidence=evidence,
        )
    with pytest.raises(ValueError):
        CoverageGap(behavior="b", suggested_test="t", file="/absolute.py")
    with pytest.raises(ValueError):
        ChunkSynthesis.model_validate({"pr_summary": {"work_type": "change"}})
    with pytest.raises(ValueError):
        ReviewResult.model_validate({**_valid_review_result_payload(), "pr_summary": {"intent": "x"}})
    with pytest.raises(ValueError):
        ReviewResult.model_validate(
            {**_valid_review_result_payload(), "good_practices": [
                {"observation": "one", "evidence": {"changedLines": [1], "whyNewInThisPr": "new"}},
                {"observation": "two", "evidence": {"changedLines": [2], "whyNewInThisPr": "new"}},
                {"observation": "three", "evidence": {"changedLines": [3], "whyNewInThisPr": "new"}},
                {"observation": "four", "evidence": {"changedLines": [4], "whyNewInThisPr": "new"}},
            ]},
        )


def test_remaining_single_pi_failure_and_result_caps(tmp_path, monkeypatch):
    import pytest
    from unittest.mock import MagicMock
    from test_reasoning import _cfg, _stage_context, _valid_review_result_payload
    from reviewforge.pipeline.schemas import GoodPractice, ReviewResult
    from reviewforge.exceptions import ReasoningEngineError

    cfg = _cfg(tmp_path)
    ctx = _stage_context(cfg, MagicMock())
    monkeypatch.setattr(single_pi, "_review_scope", lambda *args: None)
    with pytest.raises(ReasoningEngineError):
        single_pi._chunked_pass(
            single_pi,
            ctx,
            cfg,
            [SimpleNamespace(diff_text="diff", files_text="a.py\n", scope_id="scope-01")],
        )

    result = ReviewResult.model_validate(_valid_review_result_payload())
    practices = [
        GoodPractice(
            observation=f"practice-{index}",
            evidence="new",
        )
        for index in range(4)
    ]
    with pytest.raises(ValueError):
        ReviewResult.model_validate({**result.model_dump(by_alias=True), "good_practices": practices})

    from reviewforge.pipeline.schemas import EscalationHint
    hinted = result.model_copy(update={
        "escalation_hints": [
            EscalationHint(files=["a.py"], reason="risk", suggested_focus="security-audit", danger="high")
        ]
    })
    ctx.cfg = cfg.with_overrides(escalation_review_enabled=True)
    monkeypatch.setattr(single_pi, "_execute_escalation", lambda *args: None)
    assert single_pi._run_escalation_pass(single_pi, ctx, hinted) is None
