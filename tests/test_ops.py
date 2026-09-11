"""Tests for the platform-neutral container operations in ``reviewforge.ops``."""
from __future__ import annotations

import argparse
import subprocess
import tempfile
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


class TestBuildCapability:
    def test_docker_buildkit_disabled_fails_before_probe(self, monkeypatch):
        monkeypatch.setenv("DOCKER_BUILDKIT", "0")
        with pytest.raises(RuntimeError, match="DOCKER_BUILDKIT=0 disables BuildKit"):
            ops._assert_build_capable("docker")

    def test_docker_requires_buildx(self, monkeypatch):
        monkeypatch.delenv("DOCKER_BUILDKIT", raising=False)
        monkeypatch.setattr(
            ops.subprocess,
            "run",
            lambda *args, **kwargs: subprocess.CompletedProcess(args[0], 1, "", ""),
        )
        with pytest.raises(RuntimeError, match="'docker buildx' is unavailable"):
            ops._assert_build_capable("docker")

    def test_podman_requires_version_and_overrides_format(self, monkeypatch):
        monkeypatch.setenv("BUILDAH_FORMAT", "oci")
        monkeypatch.setattr(
            ops.subprocess,
            "run",
            lambda *args, **kwargs: subprocess.CompletedProcess(args[0], 0, "4.9.0\n", ""),
        )
        ops._assert_build_capable("podman")
        assert ops.os.environ["BUILDAH_FORMAT"] == "docker"

    def test_podman_rejects_old_version(self, monkeypatch):
        monkeypatch.setattr(
            ops.subprocess,
            "run",
            lambda *args, **kwargs: subprocess.CompletedProcess(args[0], 0, "3.4.0\n", ""),
        )
        with pytest.raises(RuntimeError, match="podman >= 4.0"):
            ops._assert_build_capable("podman")

    def test_podman_rejects_unparseable_version(self, monkeypatch):
        monkeypatch.setattr(
            ops.subprocess,
            "run",
            lambda *args, **kwargs: subprocess.CompletedProcess(args[0], 0, "unknown\n", ""),
        )
        with pytest.raises(RuntimeError, match="podman >= 4.0"):
            ops._assert_build_capable("podman")



    def test_cmd_build_skips_capability_probe_for_dry_run(self, monkeypatch):
        args = ops.parser().parse_args(["build", "--runtime", "docker", "--dry-run"])
        executed: list[tuple[list[str], bool]] = []
        monkeypatch.setattr(
            ops,
            "_assert_build_capable",
            lambda _runtime: pytest.fail("dry-run must not probe runtime capability"),
        )
        monkeypatch.setattr(
            ops,
            "_execute",
            lambda command, preview: executed.append((command, preview)) or 0,
        )

        assert ops.cmd_build(args) == 0
        assert executed and executed[0][0][:2] == ["docker", "build"]
        assert executed[0][1] is True

    def test_cmd_build_checks_capability_before_real_build(self, monkeypatch):
        args = ops.parser().parse_args(["build", "--runtime", "docker"])
        checked: list[str] = []
        executed: list[tuple[list[str], bool]] = []
        monkeypatch.setattr(ops, "_assert_build_capable", checked.append)
        monkeypatch.setattr(
            ops,
            "_execute",
            lambda command, preview: executed.append((command, preview)) or 0,
        )

        assert ops.cmd_build(args) == 0
        assert checked == ["docker"]
        assert executed and executed[0][1] is False


class TestEnvFile:
    def test_existing_file_used_as_is(self, tmp_path):
        env = tmp_path / ".env"
        env.write_text("A=1\n", encoding="utf-8")
        path, temporary = ops._env_file(str(env))
        assert path == str(env.resolve())
        assert temporary is False

    def test_missing_file_copies_process_env(self, tmp_path, monkeypatch):
        monkeypatch.setenv("ADO_ORG", "contoso")
        monkeypatch.setenv("UNRELATED_SECRET", "nope")
        path, temporary = ops._env_file(str(tmp_path / "absent.env"))
        try:
            assert temporary is True
            text = Path(path).read_text(encoding="utf-8")
            assert "ADO_ORG=contoso" in text
            assert "UNRELATED_SECRET=nope" not in text
        finally:
            Path(path).unlink(missing_ok=True)

    def test_values_with_newlines_are_skipped(self, tmp_path, monkeypatch):
        monkeypatch.setenv("ADO_ORG", "contoso\nINJECTED=1")
        path, temporary = ops._env_file(str(tmp_path / "absent.env"))
        try:
            text = Path(path).read_text(encoding="utf-8")
            assert "INJECTED" not in text
        finally:
            Path(path).unlink(missing_ok=True)

    def test_cmd_run_unlinks_temp_env_file_on_failure(self, tmp_path, monkeypatch):
        monkeypatch.setenv("PI_AUTH_JSON_PATH", str(tmp_path / "missing-auth.json"))

        def boom(_args):
            raise RuntimeError("run_command blew up")

        monkeypatch.setattr(ops, "_run_overrides", boom)
        args = _run_args(tmp_path, "--runtime", "docker")

        before = set(Path(tempfile.gettempdir()).glob("reviewforge-*.env"))
        with pytest.raises(RuntimeError, match="blew up"):
            ops.cmd_run(args)
        after = set(Path(tempfile.gettempdir()).glob("reviewforge-*.env"))
        assert after == before


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

    def test_crg_cache_volume_and_env_reach_container(self, tmp_path, monkeypatch):
        """The persistent CRG cache must live on its own mounted volume."""
        monkeypatch.setenv("PI_AUTH_JSON_PATH", str(tmp_path / "missing-auth.json"))
        monkeypatch.delenv("REVIEW_CRG_CACHE_VOLUME_NAME", raising=False)
        args = _run_args(tmp_path, "--runtime", "docker")

        command, _env_file, _temporary = ops.run_command(args)

        assert "reviewforge-crg-cache:/workspace/crg-cache" in command
        assert "CRG_CACHE_DIR=/workspace/crg-cache" in command
        Path(_env_file).unlink(missing_ok=True)

    def test_crg_cache_volume_name_is_overridable(self, tmp_path, monkeypatch):
        monkeypatch.setenv("PI_AUTH_JSON_PATH", str(tmp_path / "missing-auth.json"))
        monkeypatch.setenv("REVIEW_CRG_CACHE_VOLUME_NAME", "custom-crg")
        args = _run_args(tmp_path, "--runtime", "docker")

        command, _env_file, _temporary = ops.run_command(args)

        assert "custom-crg:/workspace/crg-cache" in command
        Path(_env_file).unlink(missing_ok=True)

    def test_default_run_removes_container_and_detaches(self, tmp_path, monkeypatch):
        monkeypatch.setenv("PI_AUTH_JSON_PATH", str(tmp_path / "missing-auth.json"))
        args = _run_args(tmp_path, "--runtime", "docker")
        command, env_file, _temporary = ops.run_command(args)
        try:
            assert "-d" in command
            assert "--rm" in command
        finally:
            Path(env_file).unlink(missing_ok=True)

    def test_keep_container_only_disables_removal(self, tmp_path, monkeypatch):
        monkeypatch.setenv("PI_AUTH_JSON_PATH", str(tmp_path / "missing-auth.json"))
        args = _run_args(tmp_path, "--runtime", "docker", "--keep-container")
        command, env_file, _temporary = ops.run_command(args)
        try:
            assert "-d" in command
            assert "--rm" not in command
        finally:
            Path(env_file).unlink(missing_ok=True)

    def test_restart_policy_is_added_and_retains_container(self, tmp_path, monkeypatch):
        monkeypatch.setenv("PI_AUTH_JSON_PATH", str(tmp_path / "missing-auth.json"))
        args = _run_args(tmp_path, "--runtime", "docker", "--restart", "on-failure:3")
        command, env_file, _temporary = ops.run_command(args)
        try:
            assert ["--restart", "on-failure:3"] == command[command.index("--restart"):command.index("--restart") + 2]
            assert "--rm" not in command
        finally:
            Path(env_file).unlink(missing_ok=True)

    def test_existing_running_named_container_is_reused(self, tmp_path, monkeypatch, capsys):
        args = _run_args(tmp_path, "--runtime", "docker", "--container-name", "review-pr-7")
        calls = []

        def fake_run(command, **kwargs):
            calls.append(command)
            return subprocess.CompletedProcess(command, 0, stdout="Up 2 minutes")

        monkeypatch.setattr(ops.subprocess, "run", fake_run)
        monkeypatch.setattr(ops, "_execute", lambda *_args: pytest.fail("must not launch a second container"))

        assert ops.cmd_run(args) == 0
        assert calls[0][:3] == ["docker", "ps", "--all"]
        assert "already running" in capsys.readouterr().out

    def test_existing_stopped_named_container_is_restarted(self, tmp_path, monkeypatch):
        args = _run_args(tmp_path, "--runtime", "docker", "--container-name", "review-pr-7")
        calls = []

        def fake_run(command, **kwargs):
            calls.append(command)
            return subprocess.CompletedProcess(command, 0, stdout="Exited (1) 2 minutes ago")

        monkeypatch.setattr(ops.subprocess, "run", fake_run)
        executed = []
        monkeypatch.setattr(ops, "_execute", lambda command, preview: executed.append(command) or 0)

        assert ops.cmd_run(args) == 0
        assert executed == [["docker", "restart", "review-pr-7"]]


    def test_existing_container_lookup_failure_is_reported(self, monkeypatch):
        def failed_run(*_args, **_kwargs):
            return subprocess.CompletedProcess([], 1, stdout="", stderr="daemon unavailable")

        monkeypatch.setattr(ops.subprocess, "run", failed_run)
        with pytest.raises(RuntimeError, match="failed to inspect container"):
            ops._container_status("docker", "review-pr-7")

    def test_missing_named_container_starts_new_container(self, tmp_path, monkeypatch):
        args = _run_args(tmp_path, "--runtime", "docker", "--container-name", "review-pr-7")

        monkeypatch.setattr(
            ops.subprocess,
            "run",
            lambda command, **_kwargs: subprocess.CompletedProcess(command, 0, stdout=""),
        )
        executed = []
        monkeypatch.setattr(ops, "_execute", lambda command, preview: executed.append(command) or 0)

        assert ops.cmd_run(args) == 0
        assert executed and executed[0][:2] == ["docker", "run"]

    def test_temporary_env_file_is_removed_when_command_build_fails(self, tmp_path, monkeypatch):
        env_file = tmp_path / "absent.env"
        args = _run_args(tmp_path)
        monkeypatch.setattr(ops, "runtime", lambda _explicit: (_ for _ in ()).throw(RuntimeError("runtime down")))

        with pytest.raises(RuntimeError, match="runtime down"):
            ops.run_command(args)
        assert not env_file.exists()

    def test_cache_dir_from_env_file_is_mounted_at_same_path(self, tmp_path, monkeypatch):
        monkeypatch.setenv("PI_AUTH_JSON_PATH", str(tmp_path / "missing-auth.json"))
        (tmp_path / ".env").write_text("CRG_CACHE_DIR=/custom/cache\n", encoding="utf-8")
        args = _run_args(tmp_path, "--runtime", "docker")

        command, _env_file, _temporary = ops.run_command(args)

        assert "reviewforge-crg-cache:/custom/cache" in command
        assert "CRG_CACHE_DIR=/custom/cache" in command
        Path(_env_file).unlink(missing_ok=True)

    def test_cache_dir_from_host_env_is_mounted_at_same_path(self, tmp_path, monkeypatch):
        monkeypatch.setenv("PI_AUTH_JSON_PATH", str(tmp_path / "missing-auth.json"))
        monkeypatch.setenv("CRG_CACHE_DIR", "/host-selected/cache")
        args = _run_args(tmp_path, "--runtime", "docker")

        command, _env_file, _temporary = ops.run_command(args)

        assert "reviewforge-crg-cache:/host-selected/cache" in command
        assert "CRG_CACHE_DIR=/host-selected/cache" in command
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

    def test_pr_id_selection(self, monkeypatch):
        items = [
            ("P", {"pullRequestId": 101}),
            ("P", {"pullRequestId": 202}),
            ("P", {"pullRequestId": 303}),
        ]
        monkeypatch.setattr("builtins.input", lambda _prompt: "303,101")
        selected = ops._select_pull_requests(items, interactive=True)
        assert [pr["pullRequestId"] for _p, pr in selected] == [101, 303]

    def test_mixed_id_and_index_range_selection(self, monkeypatch):
        items = [
            ("P", {"pullRequestId": 101}),
            ("P", {"pullRequestId": 202}),
            ("P", {"pullRequestId": 303}),
        ]
        monkeypatch.setattr("builtins.input", lambda _prompt: "303,1-2")
        selected = ops._select_pull_requests(items, interactive=True)
        assert [pr["pullRequestId"] for _p, pr in selected] == [101, 202, 303]

    def test_unknown_pr_id_raises(self, monkeypatch):
        monkeypatch.setattr("builtins.input", lambda _prompt: "99")
        with pytest.raises(RuntimeError, match="pull-request ID not found"):
            ops._select_pull_requests(self._items(), interactive=True)


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
