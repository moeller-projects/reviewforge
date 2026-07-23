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

ROOT = Path(__file__).resolve().parents[2]
PIN_FILE = ROOT / "versions.env"


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


def build_command(args: argparse.Namespace) -> list[str]:
    pins = load_pins(Path(args.pin_file))
    image = _value(args.image, "IMAGE_NAME", "reviewforge:latest")
    pi_version = _value(getattr(args, "pi_version", None), "PI_VERSION", pins["PI_VERSION"])
    uv_version = _value(getattr(args, "uv_version", None), "UV_VERSION", pins["UV_VERSION"])
    return [
        runtime(args.runtime), "build", "--build-arg", f"PI_VERSION={pi_version}",
        "--build-arg", f"UV_VERSION={uv_version}", "-t", image, str(ROOT),
    ]


def _env_file(path: str) -> tuple[str, bool]:
    source = Path(path)
    if source.is_file():
        return str(source.resolve()), False
    handle = tempfile.NamedTemporaryFile("w", encoding="utf-8", prefix="reviewforge-", suffix=".env", delete=False)
    try:
        handle.writelines(f"{key}={value}\n" for key, value in os.environ.items())
    finally:
        handle.close()
    return handle.name, True


def _podman_artifact_mount_source(resolved: Path) -> str:
    if resolved.drive:
        posix = resolved.as_posix()
        return f"/{resolved.drive[0].lower()}{posix[2:]}"
    return resolved.as_posix()


def run_command(args: argparse.Namespace) -> tuple[list[str], str, bool]:
    env_file, temporary = _env_file(args.env_file)
    selected_runtime = runtime(args.runtime)
    image = _value(args.image, "IMAGE_NAME", _value(None, "IMAGE", "reviewforge:latest"))
    overrides = {
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
    command = [selected_runtime, "run"]
    command.extend(["--network", "bridge", "--dns", "8.8.8.8", "--dns", "1.1.1.1"] if selected_runtime == "podman" else ["--network", "host"])
    if not args.keep_container:
        command.extend(["--rm", "-d"])
    name = _value(args.container_name, "CONTAINER_NAME") or (f"review-pr-{overrides['PR_ID']}" if overrides["PR_ID"] else None)
    if name:
        command.extend(["--name", name])
    artifact_path = _value(args.artifact_path, "ARTIFACT_PATH")
    if artifact_path:
        Path(artifact_path).mkdir(parents=True, exist_ok=True)
        resolved_artifact_path = Path(artifact_path).resolve()
        mount_source = _podman_artifact_mount_source(resolved_artifact_path) if selected_runtime == "podman" else resolved_artifact_path.as_posix()
        command.extend(["--volume", f"{mount_source}:/workspace/artifacts"])
    else:
        command.extend(["--volume", f"{_value(None, 'REVIEW_ARTIFACT_VOLUME_NAME', 'reviewforge-artifacts')}:/workspace/artifacts"])
    command.extend(["--env-file", env_file])
    for key, value in overrides.items():
        if value:
            command.extend(["-e", f"{key}={value}"])
    command.append(image)
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
    return _execute(build_command(args), args.dry_run)


def cmd_run(args: argparse.Namespace) -> int:
    if args.build:
        build = argparse.Namespace(**vars(args), pi_version=None, uv_version=None)
        if cmd_build(build):
            return 1
    command, env_file, temporary = run_command(args)
    try:
        return _execute(command, args.print_command)
    finally:
        if temporary:
            Path(env_file).unlink(missing_ok=True)


def _select_pull_requests(items: list[tuple[str, dict[str, object]]], interactive: bool) -> list[tuple[str, dict[str, object]]]:
    if not interactive:
        return items
    for index, (project, pr) in enumerate(items, start=1):
        print(f"  [{index:2}] PR #{pr['pullRequestId']}  {project}/{pr.get('repositoryId', '')} -> {pr.get('targetRefName', '')}  {pr.get('title', '')}")
    raw = input("==> Select PRs to review [all/none/1,3-5]: ").strip().lower()
    if raw in {"all", "a"}:
        return items
    if raw in {"none", "n"}:
        return []
    selected: set[int] = set()
    try:
        for part in raw.split(","):
            start, _, end = part.strip().partition("-")
            first = int(start)
            last = int(end or start)
            selected.update(range(min(first, last), max(first, last) + 1))
    except ValueError:
        raise RuntimeError("[review][ERROR] invalid selection; use all, none, or 1,3-5")
    if not selected or min(selected) < 1 or max(selected) > len(items):
        raise RuntimeError("[review][ERROR] selection is out of range")
    return [item for index, item in enumerate(items, start=1) if index in selected]


def cmd_run_open_prs(args: argparse.Namespace) -> int:
    projects = [item.strip() for item in _value(args.projects, "ADO_PROJECTS", "").split(",") if item.strip()]
    branches = [item.strip() for item in _value(args.target_branches, "ADO_TARGET_BRANCHES", "").split(",") if item.strip()]
    org = _value(args.organization, "ADO_ORGANIZATION")
    if not org or not projects or not branches:
        raise RuntimeError("[review][ERROR] ADO_ORGANIZATION, ADO_PROJECTS, and ADO_TARGET_BRANCHES are required")
    selected: list[tuple[str, dict[str, object]]] = []
    for project in projects:
        discover = [sys.executable, "-m", "reviewforge", "discover", "--org", org, "--project", project, "--target-branches", ",".join(branches)]
        result = subprocess.run(discover, check=False, capture_output=True, text=True)
        if result.returncode:
            raise RuntimeError(result.stderr.strip() or "[review][ERROR] pull-request discovery failed")
        selected.extend(
            (project, pr)
            for pr in json.loads(result.stdout)
            if not pr.get("isDraft") and pr.get("targetRefName", "").removeprefix("refs/heads/") in branches
        )
    selected.sort(key=lambda item: (item[0], str(item[1].get("repositoryId", "")), str(item[1].get("targetRefName", "")), int(item[1]["pullRequestId"])))
    if args.max_pull_requests:
        selected = selected[:args.max_pull_requests]
    selected = _select_pull_requests(selected, args.interactive or (sys.stdin.isatty() and sys.stdout.isatty()))
    if args.build:
        build = argparse.Namespace(**vars(args), pi_version=None, uv_version=None)
        if cmd_build(build):
            return 1
    failures = 0
    for project, pr in selected:
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
        failures += bool(cmd_run(argparse.Namespace(**values)))
    return int(failures > 0)


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
