"""Git checkout and diff helpers used by the reviewer."""
from __future__ import annotations

from dataclasses import dataclass
from pathlib import Path
import os
import shutil
import subprocess
import sys
import tempfile
import urllib.parse

from ..config import Config

#: A tiny ``GIT_ASKPASS`` script that supplies the ADO token when git asks
#: for credentials. The token is read from the current process environment.
GIT_ASKPASS_SCRIPT = """\
#!/usr/bin/env python3
import os, sys
print('x-access-token' if sys.argv[1].lower().find('username') >= 0 else os.environ['ADO_AUTH_TOKEN'])
"""


@dataclass
class RepoState:
    """The on-disk state of a single PR review run."""

    repo_dir: Path
    source_branch: str
    target_branch: str
    base_commit: str
    source_commit: str
    target_commit: str
    diff_text: str
    files: list[str]
    range_spec: str
    cleanup_paths: list[Path]


def log(message: str) -> None:
    print(f"[review] {message}", file=sys.stderr)


def run_git(cwd: Path, *args: str, check: bool = True) -> str:
    """Run a git command and return stdout. Raises ``SystemExit`` on failure."""
    cp = subprocess.run(
        ["git", *args],
        cwd=str(cwd),
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
    )
    if check and cp.returncode:
        raise SystemExit(
            f"[review][ERROR] git {' '.join(args)} failed: {cp.stderr.decode(errors='replace')}"
        )
    return cp.stdout.decode()


def run_logged(desc: str, cmd: list[str], cwd: Path) -> None:
    """Run a command and stream its output as ``[review][<desc>]`` lines."""
    log(desc)
    cp = subprocess.run(cmd, cwd=str(cwd), stdout=subprocess.PIPE, stderr=subprocess.PIPE)
    for stream in (cp.stdout, cp.stderr):
        for line in stream.decode(errors="replace").splitlines():
            log(f"[{desc}] {line}")
    if cp.returncode:
        raise SystemExit(f"[review][ERROR] {desc} failed with exit code {cp.returncode}")


def prepare_repo(
    cfg: Config,
    source_branch: str,
    target_branch: str,
    *,
    reviewed_commit: str | None = None,
) -> RepoState:
    """Clone the PR branches and return the safest applicable review diff.

    ``reviewed_commit`` narrows a follow-up only when it is present and an
    ancestor of the fetched source; otherwise the normal merge-base range is
    retained.
    """
    cfg.clone_root.mkdir(parents=True, exist_ok=True)
    cleanup_paths: list[Path] = []
    repo_dir = Path(tempfile.mkdtemp(prefix="repo.", dir=str(cfg.clone_root)))
    cleanup_paths.append(repo_dir)
    auth_dir = Path(tempfile.mkdtemp())
    cleanup_paths.append(auth_dir)
    askpass = auth_dir / "git-askpass.py"
    askpass.write_text(GIT_ASKPASS_SCRIPT)
    askpass.chmod(0o700)
    os.environ["GIT_ASKPASS"] = str(askpass)
    os.environ["GIT_TERMINAL_PROMPT"] = "0"
    repo_url = (
        f"https://dev.azure.com/{urllib.parse.quote(cfg.ado_org)}"
        f"/{urllib.parse.quote(cfg.ado_project)}/_git/{urllib.parse.quote(cfg.ado_repo_id)}"
    )
    log(f"initializing reviewed repo in {repo_dir}")
    run_logged("git init", ["git", "init"], repo_dir)
    run_logged(
        "git remote add origin",
        ["git", "remote", "add", "origin", repo_url],
        repo_dir,
    )
    subprocess.run(
        ["git", "config", "--global", "--add", "safe.directory", str(repo_dir)],
        cwd=str(repo_dir),
    )
    target_ref, source_ref = "refs/pr-review/target", "refs/pr-review/source"
    run_logged(
        "git fetch target",
        ["git", "fetch", "--no-tags", "--depth=200", "origin",
         f"+refs/heads/{target_branch}:{target_ref}"],
        repo_dir,
    )
    run_logged(
        "git fetch source",
        ["git", "fetch", "--no-tags", "--depth=200", "origin",
         f"+refs/heads/{source_branch}:{source_ref}"],
        repo_dir,
    )
    if subprocess.run(
        ["git", "merge-base", target_ref, source_ref],
        cwd=str(repo_dir),
        stdout=subprocess.DEVNULL,
        stderr=subprocess.DEVNULL,
    ).returncode:
        run_logged(
            "git fetch deepen target",
            ["git", "fetch", "--no-tags", "--deepen=1000", "origin",
             f"+refs/heads/{target_branch}:{target_ref}"],
            repo_dir,
        )
        run_logged(
            "git fetch deepen source",
            ["git", "fetch", "--no-tags", "--deepen=1000", "origin",
             f"+refs/heads/{source_branch}:{source_ref}"],
            repo_dir,
        )
    base = run_git(repo_dir, "merge-base", target_ref, source_ref).strip()
    target_commit = run_git(repo_dir, "rev-parse", "--verify", f"{target_ref}^{{commit}}").strip()
    source_commit = run_git(repo_dir, "rev-parse", "--verify", f"{source_ref}^{{commit}}").strip()
    log(f"target {target_branch} -> {target_commit}")
    log(f"source {source_branch} -> {source_commit}")
    log(f"merge-base -> {base}")
    run_logged("git checkout source", ["git", "checkout", source_commit], repo_dir)
    range_start = base
    if reviewed_commit:
        is_ancestor = subprocess.run(
            ["git", "merge-base", "--is-ancestor", reviewed_commit, source_commit],
            cwd=str(repo_dir),
            stdout=subprocess.DEVNULL,
            stderr=subprocess.DEVNULL,
        ).returncode == 0
        if is_ancestor:
            range_start = reviewed_commit
            log(f"follow-up range -> {range_start}..{source_commit}")
        else:
            log("previous review commit is not an ancestor; using full range")
    range_spec = f"{range_start}..{source_commit}"
    diff = run_git(repo_dir, "diff", "--unified=3", "--no-ext-diff", range_spec)
    files = [l for l in run_git(
        repo_dir, "diff", "--name-only", "--no-ext-diff", range_spec
    ).splitlines() if l]
    return RepoState(
        repo_dir,
        source_branch,
        target_branch,
        base,
        source_commit,
        target_commit,
        diff,
        files,
        range_spec,
        cleanup_paths,
    )


def cleanup(state: RepoState) -> None:
    """Remove temporary directories created by :func:`prepare_repo`."""
    for path in state.cleanup_paths:
        shutil.rmtree(path, ignore_errors=True)


__all__ = [
    "GIT_ASKPASS_SCRIPT",
    "RepoState",
    "cleanup",
    "log",
    "prepare_repo",
    "run_git",
    "run_logged",
]
