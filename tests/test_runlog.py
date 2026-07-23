from __future__ import annotations

from reviewforge.runlog import configure, info


def test_redacts_environment_secret_from_artifact_log(tmp_path, monkeypatch, capsys):
    token = "secret-token-value"
    monkeypatch.setenv("ADO_AUTH_TOKEN", token)
    path = tmp_path / "run.log"

    configure(path)
    info(f"request failed with token {token}")

    content = path.read_text(encoding="utf-8")
    assert "***" in content
    assert token not in content
    assert token not in capsys.readouterr().err


def test_persists_pi_stderr_in_run_log(tmp_path, monkeypatch):
    from types import SimpleNamespace

    from reviewforge.ai.runner import PiRunner
    from reviewforge import runlog

    prompt = tmp_path / "prompt.md"
    prompt.write_text("Review.", encoding="utf-8")
    path = tmp_path / "run.log"
    output = tmp_path / "out.json"
    configure(path)
    cfg = SimpleNamespace(
        pi_session_enabled=False,
        pi_session_clear=False,
        pi_timeout_secs=1,
        pi_model="test",
        review_language="English",
        review_run_id=None,
        pr_id="1",
        pi_session_id=None,
    )
    monkeypatch.setattr(
        "reviewforge.ai.runner.subprocess.run",
        lambda *args, **kwargs: SimpleNamespace(
            stdout=b'{"summary":"","findings":[]}', stderr=b"pi diagnostic\n", returncode=0
        ),
    )

    PiRunner(cfg).run_json(prompt, "", output, "review")

    assert "[pi review] pi diagnostic" in path.read_text(encoding="utf-8")
