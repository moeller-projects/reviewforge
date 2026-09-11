"""Tests for merge-base deepening and the --unshallow fallback in prepare_repo."""
from __future__ import annotations

import subprocess
from pathlib import Path
from types import SimpleNamespace

import pytest

from reviewforge.exceptions import GitOperationError
from reviewforge.git import ops as git_ops


class _GitSim:
    """Fake the git subprocess layer used by prepare_repo.

    Merge-base checks always fail until an ``--unshallow`` fetch has been
    logged; whether that final fetch helps is controlled by the test.
    """

    def __init__(self, monkeypatch: pytest.MonkeyPatch, *, shallow: bool, unshallow_helps: bool):
        self.shallow = shallow
        self.unshallow_helps = unshallow_helps
        self.fetched_unshallow = False
        self.logged: list[list[str]] = []

        monkeypatch.setattr(git_ops, "_repo_url", lambda _cfg: "file:///remote")
        monkeypatch.setattr(git_ops, "run_logged", self._run_logged)
        monkeypatch.setattr(git_ops, "run_git", self._run_git)
        monkeypatch.setattr(git_ops.subprocess, "run", self._run)

    def _run_logged(self, desc: str, cmd: list[str], cwd: Path) -> None:
        self.logged.append(cmd)
        if "--unshallow" in cmd:
            self.fetched_unshallow = True

    def _run_git(self, cwd: Path, *args: str, check: bool = True) -> str:
        if args[:2] == ("rev-parse", "--is-shallow-repository"):
            return "true\n" if self.shallow else "false\n"
        if args[0] == "merge-base":
            return "base123\n"
        if args[0] in {"rev-parse", "diff"}:
            return "sha256\n" if args[0] == "rev-parse" else ""
        raise AssertionError(f"unexpected run_git args: {args}")

    def _run(self, cmd, **kwargs):
        self.logged.append(cmd)
        if isinstance(cmd, list) and cmd[:2] == ["git", "merge-base"] and cmd[2] != "--is-ancestor":
            ok = self.fetched_unshallow and self.unshallow_helps
            return subprocess.CompletedProcess(cmd, 0 if ok else 1)
        return subprocess.CompletedProcess(cmd, 0)


def _cfg(tmp_path: Path) -> SimpleNamespace:
    return SimpleNamespace(clone_root=tmp_path)


class TestUnshallowFallback:
    def test_merge_base_succeeds_after_unshallow(self, tmp_path, monkeypatch):
        sim = _GitSim(monkeypatch, shallow=True, unshallow_helps=True)

        state = git_ops.prepare_repo(_cfg(tmp_path), "feature", "main")

        assert state.base_commit == "base123"
        assert any("--unshallow" in cmd for cmd in sim.logged)
        assert all("--global" not in cmd for cmd in sim.logged)
        git_ops.cleanup(state)

    def test_merge_base_failure_after_unshallow_raises(self, tmp_path, monkeypatch):
        _GitSim(monkeypatch, shallow=True, unshallow_helps=False)

        with pytest.raises(GitOperationError) as excinfo:
            git_ops.prepare_repo(_cfg(tmp_path), "feature", "main")

        message = str(excinfo.value)
        assert "'main'" in message and "'feature'" in message
        for depth in ("200", "1200", "6200", "10000", "unshallow"):
            assert depth in message
        assert excinfo.value.details["depths"] == [200, 1200, 6200, 10000, "unshallow"]

    def test_non_shallow_repo_skips_unshallow(self, tmp_path, monkeypatch):
        sim = _GitSim(monkeypatch, shallow=False, unshallow_helps=False)

        with pytest.raises(GitOperationError) as excinfo:
            git_ops.prepare_repo(_cfg(tmp_path), "feature", "main")

        assert not any("--unshallow" in cmd for cmd in sim.logged)
        assert excinfo.value.details["depths"] == [200, 1200, 6200, 10000]

    def test_failed_prepare_removes_temp_dirs(self, tmp_path, monkeypatch):
        sim = _GitSim(monkeypatch, shallow=False, unshallow_helps=False)

        with pytest.raises(GitOperationError):
            git_ops.prepare_repo(_cfg(tmp_path), "feature", "main")

        assert list(tmp_path.iterdir()) == []
        assert all("--global" not in cmd for cmd in sim.logged)


class _FetchRetrySim:
    def __init__(self):
        self.fetch_target_calls = 0

    def run_logged(self, desc: str, cmd: list[str], cwd: Path) -> None:
        if desc != "git fetch target":
            return
        self.fetch_target_calls += 1
        if self.fetch_target_calls == 1:
            raise GitOperationError(
                "[review][ERROR] git fetch target failed with exit code 128",
                details={"command": cmd, "cwd": str(cwd), "returncode": 128},
            )

    @staticmethod
    def run_git(cwd: Path, *args: str, check: bool = True) -> str:
        outputs = {"merge-base": "base123\n", "rev-parse": "sha\n", "diff": ""}
        return outputs[args[0]]

    @staticmethod
    def run(cmd, **kwargs):
        return subprocess.CompletedProcess(cmd, 0, b"", b"")


class TestFetchRetries:
    def test_transient_fetch_failure_is_retried(self, tmp_path, monkeypatch):
        monkeypatch.setattr(git_ops, "_repo_url", lambda _cfg: "file:///remote")
        sim = _FetchRetrySim()
        monkeypatch.setattr(git_ops, "run_logged", sim.run_logged)
        monkeypatch.setattr(git_ops, "run_git", sim.run_git)
        monkeypatch.setattr(git_ops.subprocess, "run", sim.run)
        state = git_ops.prepare_repo(_cfg(tmp_path), "feature", "main")
        assert sim.fetch_target_calls == 2
        git_ops.cleanup(state)

    def test_retry_switches_fetch_to_http11(self, tmp_path, monkeypatch):
        commands: list[list[str]] = []

        def fake_run_logged(desc: str, cmd: list[str], cwd: Path) -> None:
            commands.append(cmd)
            if len(commands) == 1:
                raise GitOperationError("transient disconnect")

        monkeypatch.setattr(git_ops, "run_logged", fake_run_logged)
        git_ops.run_logged_retry(
            "git fetch target",
            ["git", "fetch", "--no-tags", "origin", "refs/heads/main"],
            tmp_path,
            attempts=2,
        )

        assert commands == [
            ["git", "fetch", "--no-tags", "origin", "refs/heads/main"],
            [
                "git",
                "-c",
                "http.version=HTTP/1.1",
                "fetch",
                "--no-tags",
                "origin",
                "refs/heads/main",
            ],
        ]
    def test_final_fetch_retry_requests_blobless_pack(self, tmp_path, monkeypatch):
        commands: list[list[str]] = []

        def fake_run_logged(desc: str, cmd: list[str], cwd: Path) -> None:
            commands.append(cmd)
            if len(commands) < 3:
                raise GitOperationError("transient disconnect")

        monkeypatch.setattr(git_ops, "run_logged", fake_run_logged)
        git_ops.run_logged_retry(
            "git fetch target",
            ["git", "fetch", "--no-tags", "--depth=200", "origin", "refs/heads/main"],
            tmp_path,
        )

        assert commands[2] == [
            "git",
            "-c",
            "http.version=HTTP/1.1",
            "fetch",
            "--filter=blob:none",
            "--no-tags",
            "--depth=200",
            "origin",
            "refs/heads/main",
        ]
    def test_checkout_retry_switches_transport_without_fetch_filter(self, tmp_path, monkeypatch):
        commands: list[list[str]] = []

        def fake_run_logged(desc: str, cmd: list[str], cwd: Path) -> None:
            commands.append(cmd)
            if len(commands) < 3:
                raise GitOperationError("promisor fetch disconnect")

        monkeypatch.setattr(git_ops, "run_logged", fake_run_logged)
        git_ops.run_logged_retry(
            "git checkout source",
            ["git", "checkout", "commit"],
            tmp_path,
        )

        assert commands[1] == ["git", "-c", "http.version=HTTP/1.1", "checkout", "commit"]
        assert commands[2] == commands[1]


class TestRunGit:
    def test_failed_command_raises_git_operation_error(self, tmp_path, monkeypatch):
        monkeypatch.setattr(
            git_ops.subprocess,
            "run",
            lambda cmd, **kwargs: subprocess.CompletedProcess(cmd, 1, b"", b"fatal: nope"),
        )

        with pytest.raises(GitOperationError, match="fatal: nope"):
            git_ops.run_git(tmp_path, "status")


class TestReviewedCommitRange:
    def _install(self, monkeypatch, *, ancestor: bool):
        monkeypatch.setattr(git_ops, "_repo_url", lambda _cfg: "file:///remote")
        monkeypatch.setattr(git_ops, "run_logged", lambda desc, cmd, cwd: None)

        def fake_run_git(cwd, *args, check=True):
            if args[0] == "merge-base":
                return "base123\n"
            if args[0] == "rev-parse":
                return "sha\n"
            return ""

        monkeypatch.setattr(git_ops, "run_git", fake_run_git)

        def fake_run(cmd, **kwargs):
            if cmd[:3] == ["git", "merge-base", "--is-ancestor"]:
                return subprocess.CompletedProcess(cmd, 0 if ancestor else 1)
            return subprocess.CompletedProcess(cmd, 0)

        monkeypatch.setattr(git_ops.subprocess, "run", fake_run)

    def test_non_ancestor_reviewed_commit_uses_full_range(self, tmp_path, monkeypatch):
        self._install(monkeypatch, ancestor=False)

        state = git_ops.prepare_repo(_cfg(tmp_path), "feature", "main", reviewed_commit="oldsha")

        assert state.range_spec == "base123..sha"
        git_ops.cleanup(state)

    def test_ancestor_reviewed_commit_narrows_range(self, tmp_path, monkeypatch):
        self._install(monkeypatch, ancestor=True)

        state = git_ops.prepare_repo(_cfg(tmp_path), "feature", "main", reviewed_commit="oldsha")

        assert state.range_spec == "oldsha..sha"
        git_ops.cleanup(state)


class TestBuildChunks:
    def _state(self, files):
        return git_ops.RepoState(
            repo_dir=Path("."),
            source_branch="feature",
            target_branch="main",
            base_commit="b",
            source_commit="s",
            target_commit="t",
            diff_text="",
            files=files,
            range_spec="b..s",
            cleanup_paths=[],
        )

    def test_pending_chunk_flushed_before_oversized_file(self, monkeypatch):
        from reviewforge.git.chunker import build_chunks

        diffs = {"a.py": "small\n", "big.py": "x" * 100}
        monkeypatch.setattr(
            "reviewforge.git.chunker.run_git",
            lambda _cwd, *args: diffs[args[-1]],
        )

        chunks, truncated = build_chunks(self._state(["a.py", "big.py"]), max_bytes=50)

        assert truncated is True
        assert [c.files_text for c in chunks] == ["a.py\n", "big.py\n"]
        assert chunks[1].truncated is True
        assert "FILE DIFF TRUNCATED" in chunks[1].diff_text
