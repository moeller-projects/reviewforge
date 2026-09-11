"""Git checkout and diff helpers used by the reviewer."""
from __future__ import annotations

from dataclasses import dataclass
from pathlib import Path
import os
import shutil
import subprocess
import time
import tempfile
import urllib.parse

from ..config import Config
from ..exceptions import GitOperationError
from ..runlog import info as log
from ..ado.client import _normalize_org

#: A tiny ``GIT_ASKPASS`` script that supplies the ADO token when git asks
#: for credentials. The token is read from the current process environment.
GIT_ASKPASS_SCRIPT = """\
#!/usr/bin/env python3
import os, sys
if sys.argv[1].lower().find('username') >= 0:
    print('x-access-token')
else:
    token = (
        os.environ.get('SYSTEM_ACCESSTOKEN')
        or os.environ.get('ADO_AUTH_TOKEN')
        or os.environ.get('ADO_MCP_AUTH_TOKEN')
        or os.environ.get('ADO_API_KEY')
        or os.environ.get('AZURE_DEVOPS_EXT_PAT')
        or ''
    )
    print(token)
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




def run_git(cwd: Path, *args: str, check: bool = True) -> str:
    """Run a git command and return stdout."""
    cp = subprocess.run(
        ["git", *args],
        cwd=str(cwd),
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
    )
    if check and cp.returncode:
        stderr = cp.stderr.decode(errors="replace")
        raise GitOperationError(
            f"[review][ERROR] git {' '.join(args)} failed: {stderr}",
            details={"args": args, "cwd": str(cwd), "returncode": cp.returncode, "stderr": stderr},
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
        raise GitOperationError(
            f"[review][ERROR] {desc} failed with exit code {cp.returncode}",
            details={"command": cmd, "cwd": str(cwd), "returncode": cp.returncode},
        )



def _retry_command(cmd: list[str], attempt: int) -> list[str]:
    """Select less failure-prone Git transport options for retries."""
    if attempt == 1 or len(cmd) < 2 or cmd[:2] not in (["git", "fetch"], ["git", "checkout"]):
        return cmd
    retry = ["git", "-c", "http.version=HTTP/1.1", *cmd[1:]]
    if cmd[1] == "fetch" and attempt >= 3 and "--filter=blob:none" not in retry:
        retry.insert(retry.index("fetch") + 1, "--filter=blob:none")
    return retry

def run_logged_retry(desc: str, cmd: list[str], cwd: Path, *, attempts: int = 3) -> None:
    """Retry commands with alternate Git transport and smaller pack variants."""
    for attempt in range(1, attempts + 1):
        try:
            run_logged(desc, _retry_command(cmd, attempt), cwd)
            return
        except GitOperationError:
            if attempt == attempts:
                raise
            time.sleep(attempt)

def _repo_url(cfg: Config) -> str:
    """Return the git remote URL for the configured ADO repository."""
    org_url, _ = _normalize_org(cfg.ado_org)
    return (
        f"{org_url}/{urllib.parse.quote(cfg.ado_project)}/_git/{urllib.parse.quote(cfg.ado_repo_id)}"
    )


def _initialize_repo(
    cfg: Config, source_branch: str, target_branch: str
) -> tuple[Path, list[Path], str, str]:
    cfg.clone_root.mkdir(parents=True, exist_ok=True)
    cleanup_paths: list[Path] = []
    try:
        repo_dir = Path(tempfile.mkdtemp(prefix="repo.", dir=str(cfg.clone_root)))
        cleanup_paths.append(repo_dir)
        auth_dir = Path(tempfile.mkdtemp())
        cleanup_paths.append(auth_dir)
        askpass = auth_dir / "git-askpass.py"
        askpass.write_text(GIT_ASKPASS_SCRIPT)
        askpass.chmod(0o700)
        os.environ["GIT_ASKPASS"] = str(askpass)
        os.environ["GIT_TERMINAL_PROMPT"] = "0"
        repo_url = _repo_url(cfg)
        log(f"initializing reviewed repo in {repo_dir}")
        run_logged("git init", ["git", "init"], repo_dir)
        run_logged("git remote add origin", ["git", "remote", "add", "origin", repo_url], repo_dir)
        target_ref, source_ref = "refs/pr-review/target", "refs/pr-review/source"
        for desc, branch, ref in (
            ("git fetch target", target_branch, target_ref),
            ("git fetch source", source_branch, source_ref),
        ):
            run_logged_retry(
                desc,
                ["git", "fetch", "--no-tags", "--depth=200", "origin", f"+refs/heads/{branch}:{ref}"],
                repo_dir,
            )
        return repo_dir, cleanup_paths, target_ref, source_ref
    except Exception:
        for path in cleanup_paths:
            shutil.rmtree(path, ignore_errors=True)
        raise


def _merge_base_exists(repo_dir: Path, target_ref: str, source_ref: str) -> bool:
    return subprocess.run(
        ["git", "merge-base", target_ref, source_ref],
        cwd=str(repo_dir),
        stdout=subprocess.DEVNULL,
        stderr=subprocess.DEVNULL,
    ).returncode == 0


def _deepen_refs(
    repo_dir: Path,
    source_branch: str,
    target_branch: str,
    target_ref: str,
    source_ref: str,
    increase: int,
) -> None:
    for desc, branch, ref in (
        ("git fetch deepen target", target_branch, target_ref),
        ("git fetch deepen source", source_branch, source_ref),
    ):
        run_logged_retry(
            f"{desc} by {increase}",
            ["git", "fetch", "--no-tags", f"--deepen={increase}", "origin", f"+refs/heads/{branch}:{ref}"],
            repo_dir,
        )


def _unshallow_refs(
    repo_dir: Path, source_branch: str, target_branch: str, target_ref: str, source_ref: str
) -> None:
    for desc, branch, ref in (
        ("git fetch unshallow target", target_branch, target_ref),
        ("git fetch unshallow source", source_branch, source_ref),
    ):
        run_logged_retry(
            desc,
            ["git", "fetch", "--no-tags", "--unshallow", "origin", f"+refs/heads/{branch}:{ref}"],
            repo_dir,
        )


def _ensure_merge_base(
    repo_dir: Path,
    source_branch: str,
    target_branch: str,
    target_ref: str,
    source_ref: str,
) -> str:
    depths = [200]
    deepen = 1_000
    while not _merge_base_exists(repo_dir, target_ref, source_ref) and depths[-1] < 10_000:
        increase = min(deepen, 10_000 - depths[-1])
        next_depth = depths[-1] + increase
        log(f"merge base unavailable at depth {depths[-1]}; deepening both refs to {next_depth}")
        _deepen_refs(repo_dir, source_branch, target_branch, target_ref, source_ref, increase)
        depths.append(next_depth)
        deepen *= 5
    if _merge_base_exists(repo_dir, target_ref, source_ref):
        return run_git(repo_dir, "merge-base", target_ref, source_ref).strip()
    is_shallow = run_git(repo_dir, "rev-parse", "--is-shallow-repository").strip() == "true"
    if is_shallow:
        log("merge base unavailable after bounded deepening; fetching both refs unshallow")
        _unshallow_refs(repo_dir, source_branch, target_branch, target_ref, source_ref)
        depths_tried: list[int | str] = [*depths, "unshallow"]
    else:
        depths_tried = list(depths)
    if not _merge_base_exists(repo_dir, target_ref, source_ref):
        raise GitOperationError(
            f"[review][ERROR] no merge base found for branches "
            f"{target_branch!r} and {source_branch!r} after fetch depths {depths_tried}",
            details={"target_branch": target_branch, "source_branch": source_branch, "depths": depths_tried},
        )
    return run_git(repo_dir, "merge-base", target_ref, source_ref).strip()


def _review_range(
    repo_dir: Path, base: str, source_commit: str, reviewed_commit: str | None
) -> str:
    range_start = base
    if reviewed_commit == source_commit:
        log("previous review commit matches the current source commit; using full range")
    elif reviewed_commit and subprocess.run(
        ["git", "merge-base", "--is-ancestor", reviewed_commit, source_commit],
        cwd=str(repo_dir),
        stdout=subprocess.DEVNULL,
        stderr=subprocess.DEVNULL,
    ).returncode == 0:
        range_start = reviewed_commit
        log(f"follow-up range -> {range_start}..{source_commit}")
    elif reviewed_commit:
        log("previous review commit is not an ancestor; using full range")
    return f"{range_start}..{source_commit}"


def prepare_repo(
    cfg: Config,
    source_branch: str,
    target_branch: str,
    *,
    reviewed_commit: str | None = None,
) -> RepoState:
    """Clone the PR branches and return the safest applicable review diff."""
    repo_dir, cleanup_paths, target_ref, source_ref = _initialize_repo(
        cfg, source_branch, target_branch
    )
    try:
        base = _ensure_merge_base(
            repo_dir, source_branch, target_branch, target_ref, source_ref
        )
        target_commit = run_git(repo_dir, "rev-parse", "--verify", f"{target_ref}^{{commit}}").strip()
        source_commit = run_git(repo_dir, "rev-parse", "--verify", f"{source_ref}^{{commit}}").strip()
        log(f"target {target_branch} -> {target_commit}")
        log(f"source {source_branch} -> {source_commit}")
        log(f"merge-base -> {base}")
        range_spec = _review_range(repo_dir, base, source_commit, reviewed_commit)
        run_logged_retry("git checkout source", ["git", "checkout", source_commit], repo_dir)
        diff = run_git(repo_dir, "diff", "--unified=3", "--no-ext-diff", range_spec)
        files = [
            line for line in run_git(repo_dir, "diff", "--name-only", "--no-ext-diff", range_spec).splitlines()
            if line
        ]
    except Exception:
        for path in cleanup_paths:
            shutil.rmtree(path, ignore_errors=True)
        raise
    return RepoState(
        repo_dir, source_branch, target_branch, base, source_commit, target_commit,
        diff, files, range_spec, cleanup_paths,
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
