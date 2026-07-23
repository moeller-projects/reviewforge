from __future__ import annotations

from dataclasses import replace
from types import SimpleNamespace
from unittest.mock import MagicMock

from reviewforge.artifacts import manager
from reviewforge.config import Config
from reviewforge.pipeline.stage import StageContext, StageStatus
from reviewforge.pipeline.stages.validate_anchors import ValidateAnchorsStage


def _cfg(tmp_path) -> Config:
    prompt = tmp_path / "prompt.md"
    prompt.write_text("prompt", encoding="utf-8")
    return Config(
        ado_org="o", ado_project="p", ado_repo_id="r", pr_id="1", ado_token="t",
        source_branch="s", target_branch="m", workspace=tmp_path, clone_root=tmp_path,
        review_language="English", review_prompt_path=prompt, intent_prompt_path=prompt,
        context_plan_prompt_path=prompt, context_digest_prompt_path=prompt,
        verify_prompt_path=prompt, severity_prompt_path=prompt, standards_path=prompt,
        pi_model="m", max_diff_bytes=100, chunk_trigger_diff_bytes=100,
        disable_chunk_review=False, pi_timeout_secs=1, dry_run=True,
        include_work_items=True, include_existing_comments=True, verify_findings=True,
        force_review=False, review_target_branches="", review_artifact_dir=None,
        review_artifact_root=tmp_path / "artifacts", review_run_id="r1",
    )


def _ctx(tmp_path, policy="downgrade"):
    cfg = replace(_cfg(tmp_path), anchor_policy=policy)
    artifacts = manager.create(cfg)
    state = SimpleNamespace(diff_text="diff --git a/a.py b/a.py\n--- a/a.py\n+++ b/a.py\n@@ -3,1 +3,1 @@\n-old\n+new\n")
    ctx = StageContext(cfg=cfg, artifacts=artifacts, state=state, pi=MagicMock())
    ctx.final = {"summary": "s", "findings": [
        {"title": "valid", "file": "a.py", "line": 3},
        {"title": "shifted", "file": "a.py", "line": 99},
        {"title": "Work item #1 scope", "file": "gone.py", "line": 9},
        {"title": "general", "file": None, "line": None},
    ]}
    return ctx


def test_downgrades_invalid_anchor_and_keeps_exempt_findings(tmp_path):
    ctx = _ctx(tmp_path)
    result = ValidateAnchorsStage()(ctx)
    findings = ctx.final["findings"]

    assert result.status == StageStatus.OK
    assert result.details == {"downgraded": 1, "dropped": 0}
    assert findings[0]["line"] == 3
    assert findings[1]["file"] is None and findings[1]["anchorDowngraded"] is True
    assert findings[2]["file"] == "gone.py"


def test_drop_removes_invalid_anchor(tmp_path):
    ctx = _ctx(tmp_path, "drop")
    ValidateAnchorsStage()(ctx)
    assert [f["title"] for f in ctx.final["findings"]] == ["valid", "Work item #1 scope", "general"]


def test_off_is_noop(tmp_path):
    ctx = _ctx(tmp_path, "off")
    result = ValidateAnchorsStage()(ctx)
    assert result.status == StageStatus.SKIPPED
    assert ctx.final["findings"][1]["line"] == 99


def test_validation_stage_precedes_posting():
    from reviewforge.pipeline.stages import DEFAULT_PIPELINE, PostToAdoStage, ValidateAnchorsStage

    names = [type(stage) for stage in DEFAULT_PIPELINE]
    assert names.index(ValidateAnchorsStage) == names.index(PostToAdoStage) - 1


def test_drop_mirrors_canonical_discard(tmp_path):
    ctx = _ctx(tmp_path, "drop")

    class Result:
        def __init__(self):
            self.findings = [SimpleNamespace(file="a.py", line=99, title="shifted")]
            self.discarded_findings = []

        def model_dump(self, **_kwargs):
            return {}

    ctx.review_result = Result()
    ValidateAnchorsStage()(ctx)
    assert ctx.review_result.findings == []
    assert ctx.review_result.discarded_findings[0].reason == "anchor not present in diff"
