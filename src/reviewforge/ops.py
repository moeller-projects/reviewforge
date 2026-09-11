"""Platform-neutral container operations for ReviewForge."""
from __future__ import annotations

import argparse
import json
import os
import re
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
from typing import Iterable
from .config import parse_dotenv


ROOT = Path(__file__).resolve().parents[2]
PIN_FILE = ROOT / "versions.env"
_ENV_ALLOWLIST_PREFIXES: tuple[str, ...] = (
    "ADO_",
    "PR_",
    "REVIEW_",
    "PI_",
    "CHUNK_",
    "MAX_",
    "DISABLE_",
    "DRY_",
    "FORCE_",
    "INCLUDE_",
    "VERIFY_",
    "FAIL_",
    "VOTE_",
    "CONTEXT_",
    "COLLECT_",
    "AC_",
    "REASONING_",
    "FAST_",
    "ANCHOR_",
    "CRG_",
    "GRAPH_",
)
_ENV_ALLOWLIST_KEYS: set[str] = {
    "SYSTEM_ACCESSTOKEN",
    "OPENAI_API_KEY",
    "WORKSPACE",
    "CLONE_ROOT",
    "MODEL_BACKEND",
    "COMMIT_CONTEXT_MAX",
    "IMAGE",
    "IMAGE_NAME",
}


def _value(explicit: str | None, name: str, default: str | None = None) -> str | None:
    return explicit or os.environ.get(name) or default


def load_pins(path: Path = PIN_FILE) -> dict[str, str]:
    """Load required checked-in build pins."""
    if not path.is_file():
        raise RuntimeError(f"[review][ERROR] version pin file missing: {path}")
    pins = {
        key: value
        for line in path.read_text(encoding="utf-8").splitlines()
        if "=" in line and not line.lstrip().startswith("#")
        for key, value in [line.split("=", 1)]
    }
    missing = [key for key in ("PI_VERSION", "UV_VERSION", "PI_MODEL") if not pins.get(key)]
    if missing:
        raise RuntimeError(f"[review][ERROR] version pin file missing values: {', '.join(missing)}")
    return pins


def runtime(explicit: str | None = None) -> str:
    if explicit:
        return explicit
    for candidate in ("docker", "podman"):
        if shutil.which(candidate):
            return candidate
    raise RuntimeError("[review][ERROR] neither docker nor podman found on PATH")


def _assert_docker_buildkit() -> None:
    if os.environ.get("DOCKER_BUILDKIT") == "0":
        raise RuntimeError(
            "[review][ERROR] DOCKER_BUILDKIT=0 disables BuildKit, which the "
            "Dockerfile requires (cache/bind mounts). Unset it or set "
            "DOCKER_BUILDKIT=1."
        )
    probe = subprocess.run(["docker", "buildx", "version"], capture_output=True, text=True, check=False)
    if probe.returncode != 0:
        raise RuntimeError(
            "[review][ERROR] 'docker buildx' is unavailable. Install Docker >= 23.0 "
            "(BuildKit is required for the Dockerfile's cache/bind mounts)."
        )


def _assert_podman_buildah() -> None:
    probe = subprocess.run(
        ["podman", "version", "--format", "{{.Client.Version}}"],
        capture_output=True, text=True, check=False,
    )
    try:
        major = int((probe.stdout.strip() or "0").split(".")[0])
    except ValueError:
        major = 0
    if probe.returncode != 0 or major < 4:
        raise RuntimeError(
            "[review][ERROR] podman >= 4.0 (buildah >= 1.24) is required for the "
            "Dockerfile's cache/bind mounts."
        )
    os.environ["BUILDAH_FORMAT"] = "docker"


def _assert_build_capable(selected_runtime: str) -> None:
    """Fail loudly before a build that requires BuildKit cache/bind mounts."""
    if selected_runtime == "docker":
        _assert_docker_buildkit()
    else:
        _assert_podman_buildah()

def build_command(args: argparse.Namespace) -> list[str]:
    pins = load_pins(Path(args.pin_file))
    image = _value(args.image, "IMAGE_NAME", "reviewforge:latest")
    pi_version = _value(getattr(args, "pi_version", None), "PI_VERSION", pins["PI_VERSION"])
    uv_version = _value(getattr(args, "uv_version", None), "UV_VERSION", pins["UV_VERSION"])
    selected_runtime = runtime(args.runtime)
    return [
        selected_runtime, "build", "--build-arg", f"PI_VERSION={pi_version}",
        "--build-arg", f"UV_VERSION={uv_version}", "-t", image, str(ROOT),
    ]


def _env_file(path: str) -> tuple[str, bool]:
    source = Path(path)
    if source.is_file():
        return str(source.resolve()), False
    handle = tempfile.NamedTemporaryFile("w", encoding="utf-8", prefix="reviewforge-", suffix=".env", delete=False)
    try:
        for key, value in os.environ.items():
            if key in _ENV_ALLOWLIST_KEYS or key.startswith(_ENV_ALLOWLIST_PREFIXES):
                if "\n" in value or "\r" in value:
                    continue
                handle.write(f"{key}={value}\n")
    finally:
        handle.close()
    return handle.name, True


def _podman_artifact_mount_source(resolved: Path) -> str:
    if resolved.drive:
        posix = resolved.as_posix()
        return f"/{resolved.drive[0].lower()}{posix[2:]}"
    return resolved.as_posix()


def _auth_json_mount_source() -> str | None:
    auth_json = Path(_value(None, "PI_AUTH_JSON_PATH", str(Path.home() / ".pi" / "agent" / "auth.json"))).expanduser()
    if not auth_json.is_file():
        return None
    return _podman_artifact_mount_source(auth_json.resolve())



def _run_overrides(args: argparse.Namespace) -> dict[str, str | None]:
    return {
        "ADO_AUTH_TOKEN": _value(args.ado_token, "ADO_AUTH_TOKEN", os.environ.get("ADO_API_KEY")),
        "PR_URL": _value(args.pr_url, "PR_URL"),
        "ADO_ORG": _value(args.org, "ADO_ORG"),
        "ADO_PROJECT": _value(args.project, "ADO_PROJECT"),
        "ADO_REPO_ID": _value(args.repo_id, "ADO_REPO_ID"),
        "PR_ID": _value(args.pr_id, "PR_ID"),
        "REVIEW_LANGUAGE": _value(args.language, "REVIEW_LANGUAGE", "English"),
        "FAIL_ON": _value(args.fail_on, "FAIL_ON", "none"),
        "VOTE_WAITING_ON": _value(args.vote_waiting_on, "VOTE_WAITING_ON", "minor"),
        "PI_MODEL": _value(args.pi_model, "PI_MODEL", load_pins(Path(args.pin_file))["PI_MODEL"]),
        "DRY_RUN": "1" if args.dry_run else _value(None, "DRY_RUN"),
    }


def _append_mounts(
    command: list[str],
    args: argparse.Namespace,
    selected_runtime: str,
    overrides: dict[str, str | None],
    env_file: str,
) -> None:
    auth_json_mount = _auth_json_mount_source()
    if auth_json_mount:
        command.extend(["--volume", f"{auth_json_mount}:/home/review/.pi/agent/auth.json:ro"])
    artifact_path = _value(args.artifact_path, "ARTIFACT_PATH")
    if artifact_path:
        Path(artifact_path).mkdir(parents=True, exist_ok=True)
        resolved = Path(artifact_path).resolve()
        mount_source = _podman_artifact_mount_source(resolved) if selected_runtime == "podman" else resolved.as_posix()
        command.extend(["--volume", f"{mount_source}:/workspace/artifacts"])
    else:
        volume = _value(None, "REVIEW_ARTIFACT_VOLUME_NAME", "reviewforge-artifacts")
        command.extend(["--volume", f"{volume}:/workspace/artifacts"])
    dotenv = parse_dotenv(env_file)
    cache_dir = os.environ.get("CRG_CACHE_DIR") or dotenv.get("CRG_CACHE_DIR") or "/workspace/crg-cache"
    cache_volume = _value(None, "REVIEW_CRG_CACHE_VOLUME_NAME", "reviewforge-crg-cache")
    command.extend(["--volume", f"{cache_volume}:{cache_dir}", "-e", f"CRG_CACHE_DIR={cache_dir}"])
    command.extend(["--env-file", env_file])
    for key, value in overrides.items():
        if value:
            command.extend(["-e", f"{key}={value}"])


def _container_name(args: argparse.Namespace) -> str | None:
    overrides = _run_overrides(args)
    return _value(args.container_name, "CONTAINER_NAME") or (
        f"review-pr-{overrides['PR_ID']}" if overrides["PR_ID"] else None
    )


def _container_status(selected_runtime: str, name: str) -> str | None:
    result = subprocess.run(
        [
            selected_runtime,
            "ps",
            "--all",
            "--filter",
            f"name=^{name}$",
            "--format",
            "{{.Status}}",
        ],
        check=False,
        capture_output=True,
        text=True,
    )
    if result.returncode:
        raise RuntimeError(f"[review][ERROR] failed to inspect container {name!r}")
    return result.stdout.strip() or None


def _append_run_options(
    command: list[str],
    args: argparse.Namespace,
    selected_runtime: str,
    overrides: dict[str, str | None],
    env_file: str,
) -> None:
    command.extend(
        ["--network", "bridge", "--dns", "8.8.8.8", "--dns", "1.1.1.1"]
        if selected_runtime == "podman"
        else ["--network", "host"]
    )
    command.append("-d")
    if args.restart:
        command.extend(["--restart", args.restart])
    elif not args.keep_container:
        command.append("--rm")
    name = _value(args.container_name, "CONTAINER_NAME") or (
        f"review-pr-{overrides['PR_ID']}" if overrides["PR_ID"] else None
    )
    if name:
        command.extend(["--name", name])
    _append_mounts(command, args, selected_runtime, overrides, env_file)


def run_command(args: argparse.Namespace) -> tuple[list[str], str, bool]:
    env_file, temporary = _env_file(args.env_file)
    try:
        selected_runtime = runtime(args.runtime)
        image = _value(args.image, "IMAGE_NAME", _value(None, "IMAGE", "reviewforge:latest"))
        overrides = _run_overrides(args)
        command = [selected_runtime, "run"]
        _append_run_options(command, args, selected_runtime, overrides, env_file)
        command.append(image)
    except Exception:
        if temporary:
            Path(env_file).unlink(missing_ok=True)
        raise
    return command, env_file, temporary


def _redact_command(command: list[str]) -> str:
    redacted = command.copy()
    for index, token in enumerate(redacted[:-1]):
        if token != "-e":
            continue
        key, sep, _ = redacted[index + 1].partition("=")
        if sep and re.search(r"(token|password|secret|key)", key, re.IGNORECASE):
            redacted[index + 1] = f"{key}=***"
    return " ".join(redacted)


def _execute(command: list[str], preview: bool) -> int:
    print(_redact_command(command))
    return 0 if preview else subprocess.run(command, check=False).returncode


def cmd_build(args: argparse.Namespace) -> int:
    command = build_command(args)
    if not args.dry_run:
        _assert_build_capable(command[0])
    return _execute(command, args.dry_run)
def _reuse_existing_container(
    args: argparse.Namespace, selected_runtime: str, name: str | None
) -> int | None:
    if not name or args.dry_run:
        return None
    status = _container_status(selected_runtime, name)
    if not status:
        return None
    if status.lower().startswith("up "):
        print(f"{selected_runtime} container {name} is already running")
        return 0
    return _execute([selected_runtime, "restart", name], args.print_command)


def cmd_run(args: argparse.Namespace) -> int:
    if args.build:
        build = argparse.Namespace(**vars(args), pi_version=None, uv_version=None)
        if cmd_build(build):
            return 1
    selected_runtime = runtime(args.runtime)
    existing = None if args.print_command else _reuse_existing_container(args, selected_runtime, _container_name(args))
    if existing is not None:
        return existing
    command, env_file, temporary = run_command(args)
    try:
        return _execute(command, args.print_command)
    finally:
        if temporary:
            Path(env_file).unlink(missing_ok=True)


def _selection_token_indices(
    token: str, pull_request_ids: dict[int, int]
) -> set[int]:
    if token.startswith("#"):
        if not token[1:].isdigit():
            raise ValueError(token)
        pull_request_id = int(token[1:])
        if pull_request_id not in pull_request_ids:
            raise RuntimeError(
                f"[review][ERROR] pull-request ID not found: {pull_request_id}"
            )
        return {pull_request_ids[pull_request_id]}
    start, separator, end = token.partition("-")
    if separator:
        first = int(start)
        last = int(end)
        return set(range(min(first, last), max(first, last) + 1))
    return {int(token)}


def _selection_indices(
    raw: str, items: list[tuple[str, dict[str, object]]]
) -> set[int]:
    selected: set[int] = set()
    size = len(items)
    pull_request_ids = {
        int(pr["pullRequestId"]): index
        for index, (_project, pr) in enumerate(items, start=1)
    }
    try:
        for part in raw.split(","):
            selected.update(_selection_token_indices(part.strip(), pull_request_ids))
    except ValueError as exc:
        raise RuntimeError(
            "[review][ERROR] invalid selection; use all, none, indexes, "
            "index ranges, or #<PR ID>"
        ) from exc
    if not selected or min(selected) < 1 or max(selected) > size:
        raise RuntimeError("[review][ERROR] selection index is out of range")
    return selected


def _select_pull_requests(items: list[tuple[str, dict[str, object]]], interactive: bool) -> list[tuple[str, dict[str, object]]]:
    if not interactive:
        return items
    for index, (project, pr) in enumerate(items, start=1):
        print(f"  [{index:2}] PR #{pr['pullRequestId']}  {project}/{pr.get('repositoryId', '')} -> {pr.get('targetRefName', '')}  {pr.get('title', '')}")
    raw = input("==> Select PRs [all/none/indexes/index ranges/#PR-ID]: ").strip().lower()
    if raw in {"all", "a"}:
        return items
    if raw in {"none", "n"}:
        return []
    selected = _selection_indices(raw, items)
    return [item for index, item in enumerate(items, start=1) if index in selected]


def _discover_project(
    org: str, project: str, branches: list[str]
) -> list[tuple[str, dict[str, object]]]:
    discover = [
        sys.executable, "-m", "reviewforge", "discover",
        "--org", org, "--project", project, "--target-branches", ",".join(branches),
    ]
    result = subprocess.run(discover, check=False, capture_output=True, text=True)
    if result.returncode:
        raise RuntimeError(result.stderr.strip() or "[review][ERROR] pull-request discovery failed")
    return [
        (project, pr)
        for pr in json.loads(result.stdout)
        if not pr.get("isDraft")
        and pr.get("targetRefName", "").removeprefix("refs/heads/") in branches
    ]


def _run_open_pr(
    args: argparse.Namespace, org: str, project: str, pr: dict[str, object]
) -> int:
    values = vars(args).copy()
    values.update(
        org=org,
        project=project,
        repo_id=str(pr["repositoryId"]),
        pr_id=str(pr["pullRequestId"]),
        pr_url=None,
        language=None,
        fail_on=None,
        vote_waiting_on=None,
        pi_model=None,
        container_name=None,
        artifact_path=None,
        build=False,
    )
    return cmd_run(argparse.Namespace(**values))


def _run_selected_open_prs(
    args: argparse.Namespace, org: str, selected: list[tuple[str, dict[str, object]]]
) -> int:
    failures = sum(
        bool(_run_open_pr(args, org, project, pr)) for project, pr in selected
    )
    return int(failures > 0)


def _selected_open_prs(
    projects: list[str],
    branches: list[str],
    org: str,
    limit: int,
    interactive: bool,
) -> list[tuple[str, dict[str, object]]]:
    selected = [item for project in projects for item in _discover_project(org, project, branches)]
    selected.sort(
        key=lambda item: (
            item[0],
            str(item[1].get("repositoryId", "")),
            str(item[1].get("targetRefName", "")),
            int(item[1]["pullRequestId"]),
        )
    )
    if limit:
        selected = selected[:limit]
    return _select_pull_requests(selected, interactive)


def cmd_run_open_prs(args: argparse.Namespace) -> int:
    projects = [item.strip() for item in _value(args.projects, "ADO_PROJECTS", "").split(",") if item.strip()]
    branches = [item.strip() for item in _value(args.target_branches, "ADO_TARGET_BRANCHES", "").split(",") if item.strip()]
    org = _value(args.organization, "ADO_ORGANIZATION")
    if not org or not projects or not branches:
        raise RuntimeError("[review][ERROR] ADO_ORGANIZATION, ADO_PROJECTS, and ADO_TARGET_BRANCHES are required")
    selected = _selected_open_prs(projects, branches, org, args.max_pull_requests, args.interactive)
    if args.build:
        build = argparse.Namespace(**vars(args), pi_version=None, uv_version=None)
        if cmd_build(build):
            return 1
    return _run_selected_open_prs(args, org, selected)


def parser() -> argparse.ArgumentParser:
    common = argparse.ArgumentParser(add_help=False)
    common.add_argument("--runtime")
    common.add_argument("--pin-file", default=str(PIN_FILE))
    common.add_argument("--image")
    build = argparse.ArgumentParser(add_help=False, parents=[common])
    build.add_argument("--pi-version")
    build.add_argument("--uv-version")
    build.add_argument("--dry-run", action="store_true")
    run = argparse.ArgumentParser(add_help=False, parents=[common])
    run.add_argument("--restart", default=None, help="container restart policy, e.g. on-failure:3")
    run.add_argument("--pr-url")
    run.add_argument("--org")
    run.add_argument("--project")
    run.add_argument("--repo-id")
    run.add_argument("--pr-id")
    run.add_argument("--ado-token")
    run.add_argument("--language")
    run.add_argument("--fail-on")
    run.add_argument("--vote-waiting-on")
    run.add_argument("--pi-model")
    run.add_argument("--env-file", default=".env")
    run.add_argument("--container-name")
    run.add_argument("--artifact-path")
    run.add_argument("--dry-run", action="store_true")
    run.add_argument("--print-command", action="store_true")
    run.add_argument("--build", action="store_true")
    run.add_argument("--keep-container", action="store_true")
    root = argparse.ArgumentParser(description=__doc__)
    commands = root.add_subparsers(dest="command", required=True)
    commands.add_parser("build", parents=[build]).set_defaults(func=cmd_build)
    commands.add_parser("run", parents=[run]).set_defaults(func=cmd_run)
    batch = commands.add_parser("run-open-prs", parents=[run])
    batch.add_argument("--organization")
    batch.add_argument("--projects")
    batch.add_argument("--target-branches")
    batch.add_argument("--max-pull-requests", type=int, default=0)
    batch.add_argument("--interactive", action="store_true")
    batch.set_defaults(func=cmd_run_open_prs)
    return root


def main(argv: Iterable[str] | None = None) -> int:
    try:
        args = parser().parse_args(argv)
        return int(args.func(args))
    except RuntimeError as exc:
        print(exc, file=sys.stderr)
        return 2


if __name__ == "__main__":
    raise SystemExit(main())
