"""Tests for the platform-neutral container operations in ``reviewforge.ops``."""
from __future__ import annotations

import argparse
import subprocess
from pathlib import Path, PureWindowsPath

import pytest

from reviewforge import ops


def _run_args(tmp_path: Path, *extra: str) -> argparse.Namespace:
    return ops.parser().parse_args(["run", "--env-file", str(tmp_path / ".env"), *extra])


class TestLoadPins:
    def test_missing_file_raises(self, tmp_path):
        with pytest.raises(RuntimeError, match="pin file missing"):
            ops.load_pins(tmp_path / "nope.env")

    def test_missing_values_raise(self, tmp_path):
        pins = tmp_path / "versions.env"
        pins.write_text("PI_VERSION=1\n", encoding="utf-8")
        with pytest.raises(RuntimeError, match="missing values: UV_VERSION, PI_MODEL"):
            ops.load_pins(pins)


class TestRuntime:
    def test_explicit_wins(self):
        assert ops.runtime("podman") == "podman"

    def test_detects_available_binary(self, monkeypatch):
        monkeypatch.setattr(ops.shutil, "which", lambda name: f"/usr/bin/{name}" if name == "podman" else None)
        assert ops.runtime(None) == "podman"

    def test_no_runtime_raises(self, monkeypatch):
        monkeypatch.setattr(ops.shutil, "which", lambda _name: None)
        with pytest.raises(RuntimeError, match="neither docker nor podman"):
            ops.runtime(None)


class TestEnvFile:
    def test_existing_file_used_as_is(self, tmp_path):
        env = tmp_path / ".env"
        env.write_text("A=1\n", encoding="utf-8")
        path, temporary = ops._env_file(str(env))
        assert path == str(env.resolve())
        assert temporary is False

    def test_missing_file_copies_process_env(self, tmp_path, monkeypatch):
        monkeypatch.setenv("REVIEWFORGE_TEST_MARKER", "yes")
        path, temporary = ops._env_file(str(tmp_path / "absent.env"))
        try:
            assert temporary is True
            assert "REVIEWFORGE_TEST_MARKER=yes" in Path(path).read_text(encoding="utf-8")
        finally:
            Path(path).unlink(missing_ok=True)


class TestMountSources:
    def test_windows_drive_becomes_podman_path(self):
        assert ops._podman_artifact_mount_source(PureWindowsPath("D:/work/artifacts")) == "/d/work/artifacts"

    def test_auth_json_absent_returns_none(self, monkeypatch, tmp_path):
        monkeypatch.setenv("PI_AUTH_JSON_PATH", str(tmp_path / "missing-auth.json"))
        assert ops._auth_json_mount_source() is None

    def test_auth_json_present_returns_path(self, monkeypatch, tmp_path):
        auth = tmp_path / "auth.json"
        auth.write_text("{}", encoding="utf-8")
        monkeypatch.setenv("PI_AUTH_JSON_PATH", str(auth))
        assert ops._auth_json_mount_source() == str(auth.resolve())


class TestRunCommand:
    def test_artifact_path_is_mounted(self, tmp_path, monkeypatch):
        monkeypatch.setenv("PI_AUTH_JSON_PATH", str(tmp_path / "missing-auth.json"))
        artifacts = tmp_path / "artifacts"
        args = _run_args(tmp_path, "--runtime", "docker", "--artifact-path", str(artifacts))

        command, _env_file, temporary = ops.run_command(args)

        assert temporary is True
        assert artifacts.is_dir()
        volume = f"{artifacts.resolve().as_posix()}:/workspace/artifacts"
        assert volume in command
        Path(_env_file).unlink(missing_ok=True)

    def test_named_volume_used_without_artifact_path(self, tmp_path, monkeypatch):
        monkeypatch.setenv("PI_AUTH_JSON_PATH", str(tmp_path / "missing-auth.json"))
        args = _run_args(tmp_path, "--runtime", "podman")

        command, _env_file, _temporary = ops.run_command(args)

        assert "reviewforge-artifacts:/workspace/artifacts" in command
        assert "--network" in command and "bridge" in command
        Path(_env_file).unlink(missing_ok=True)


class TestRedactCommand:
    def test_secret_values_are_masked(self):
        redacted = ops._redact_command(["docker", "run", "-e", "ADO_AUTH_TOKEN=hunter2", "-e", "PR_ID=7", "img"])
        assert "ADO_AUTH_TOKEN=***" in redacted
        assert "hunter2" not in redacted
        assert "PR_ID=7" in redacted


class TestExecute:
    def test_preview_prints_without_running(self, capsys):
        assert ops._execute(["docker", "run", "-e", "ADO_AUTH_TOKEN=x", "img"], preview=True) == 0
        out = capsys.readouterr().out
        assert "docker run" in out

    def test_real_run_returns_exit_code(self, monkeypatch):
        monkeypatch.setattr(
            ops.subprocess, "run",
            lambda cmd, check=False: subprocess.CompletedProcess(cmd, 3),
        )
        assert ops._execute(["docker", "run", "img"], preview=False) == 3


class TestCmdRun:
    def test_failed_build_short_circuits(self, tmp_path, monkeypatch):
        monkeypatch.setattr(ops, "cmd_build", lambda _args: 1)
        args = _run_args(tmp_path, "--runtime", "docker", "--build")
        assert ops.cmd_run(args) == 1

    def test_temporary_env_file_is_removed(self, tmp_path, monkeypatch):
        monkeypatch.setenv("PI_AUTH_JSON_PATH", str(tmp_path / "missing-auth.json"))
        env_file = tmp_path / "absent.env"
        created: list[str] = []
        real_execute = ops._execute

        def spy(command, preview):
            created.append(command[command.index("--env-file") + 1])
            return real_execute(command, preview)

        monkeypatch.setattr(ops, "_execute", spy)
        args = _run_args(tmp_path, "--runtime", "docker", "--print-command")

        assert ops.cmd_run(args) == 0
        assert created and not Path(created[0]).exists()
        assert not env_file.exists()


class TestSelectPullRequests:
    def _items(self):
        return [("P", {"pullRequestId": i, "repositoryId": "r", "targetRefName": "refs/heads/main", "title": f"t{i}"}) for i in (1, 2, 3)]

    def test_non_interactive_returns_all(self):
        assert ops._select_pull_requests(self._items(), interactive=False) == self._items()

    def test_cmd_run_open_prs_does_not_prompt_when_tty(self, tmp_path, monkeypatch):
        monkeypatch.setattr(
            ops.subprocess,
            "run",
            lambda cmd, **kwargs: subprocess.CompletedProcess(
                cmd,
                0,
                '[{"pullRequestId": 1, "repositoryId": "r", "targetRefName": "refs/heads/main", "title": "t1", "isDraft": false}, {"pullRequestId": 2, "repositoryId": "r", "targetRefName": "refs/heads/main", "title": "t2", "isDraft": false}]',
                "",
            ),
        )

        class _TTY:
            @staticmethod
            def isatty() -> bool:
                return True

        monkeypatch.setattr(ops.sys, "stdin", _TTY())
        monkeypatch.setattr(ops.sys, "stdout", _TTY())
        monkeypatch.setattr("builtins.input", lambda _prompt: (_ for _ in ()).throw(AssertionError("prompted")))
        selected: list[str] = []
        monkeypatch.setattr(ops, "cmd_run", lambda args: selected.append(args.pr_id) or 0)
        args = ops.parser().parse_args(
            [
                "run-open-prs",
                "--env-file",
                str(tmp_path / ".env"),
                "--organization",
                "contoso",
                "--projects",
                "P",
                "--target-branches",
                "main",
            ]
        )
        assert ops.cmd_run_open_prs(args) == 0
        assert selected == ["1", "2"]


    def test_all_and_none(self, monkeypatch, capsys):
        monkeypatch.setattr("builtins.input", lambda _prompt: "all")
        assert ops._select_pull_requests(self._items(), interactive=True) == self._items()
        monkeypatch.setattr("builtins.input", lambda _prompt: "none")
        assert ops._select_pull_requests(self._items(), interactive=True) == []

    def test_range_selection(self, monkeypatch, capsys):
        monkeypatch.setattr("builtins.input", lambda _prompt: "1,2-3")
        selected = ops._select_pull_requests(self._items(), interactive=True)
        assert [pr["pullRequestId"] for _p, pr in selected] == [1, 2, 3]
        monkeypatch.setattr("builtins.input", lambda _prompt: "2")
        selected = ops._select_pull_requests(self._items(), interactive=True)
        assert [pr["pullRequestId"] for _p, pr in selected] == [2]

    def test_invalid_selection_raises(self, monkeypatch, capsys):
        monkeypatch.setattr("builtins.input", lambda _prompt: "banana")
        with pytest.raises(RuntimeError, match="invalid selection"):
            ops._select_pull_requests(self._items(), interactive=True)

    def test_out_of_range_raises(self, monkeypatch, capsys):
        monkeypatch.setattr("builtins.input", lambda _prompt: "9")
        with pytest.raises(RuntimeError, match="out of range"):
            ops._select_pull_requests(self._items(), interactive=True)


class TestRunOpenPrs:
    def _args(self, tmp_path: Path, *extra: str) -> argparse.Namespace:
        return ops.parser().parse_args(
            ["run-open-prs", "--env-file", str(tmp_path / ".env"), *extra]
        )

    def test_missing_configuration_raises(self, tmp_path, monkeypatch):
        for name in ("ADO_ORGANIZATION", "ADO_PROJECTS", "ADO_TARGET_BRANCHES"):
            monkeypatch.delenv(name, raising=False)
        with pytest.raises(RuntimeError, match="ADO_ORGANIZATION, ADO_PROJECTS, and ADO_TARGET_BRANCHES are required"):
            ops.cmd_run_open_prs(self._args(tmp_path))

    def test_discovery_failure_raises(self, tmp_path, monkeypatch):
        monkeypatch.setattr(
            ops.subprocess, "run",
            lambda cmd, **kwargs: subprocess.CompletedProcess(cmd, 1, "", "boom"),
        )
        args = self._args(tmp_path, "--organization", "contoso", "--projects", "P", "--target-branches", "main")
        with pytest.raises(RuntimeError, match="boom"):
            ops.cmd_run_open_prs(args)

    def test_failed_build_short_circuits(self, tmp_path, monkeypatch):
        monkeypatch.setattr(
            ops.subprocess, "run",
            lambda cmd, **kwargs: subprocess.CompletedProcess(cmd, 0, "[]", ""),
        )
        monkeypatch.setattr(ops, "cmd_build", lambda _args: 1)
        args = self._args(
            tmp_path,
            "--organization", "contoso", "--projects", "P", "--target-branches", "main", "--build",
        )
        assert ops.cmd_run_open_prs(args) == 1

    def test_main_returns_2_on_runtime_error(self, tmp_path, monkeypatch):
        for name in ("ADO_ORGANIZATION", "ADO_PROJECTS", "ADO_TARGET_BRANCHES"):
            monkeypatch.delenv(name, raising=False)
        assert ops.main(["run-open-prs", "--env-file", str(tmp_path / ".env")]) == 2
