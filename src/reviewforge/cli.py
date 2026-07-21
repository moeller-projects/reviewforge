"""Command-line interface for the reviewforge.

Subcommands:

* ``review``        — generate findings (and post them, by default).
* ``post``          — post a previously generated review.
* ``open-prs``      — list active PRs awaiting your review.
* ``validate-config`` — validate the configuration and exit.

All subcommands share the same ``--org / --project / --repo / --pr`` flag
shape. CLI flags override environment variables and ``.env`` values.
"""
from __future__ import annotations

import argparse
import json
import sys
from pathlib import Path
from typing import Any, Sequence

from .ado.client import resolve_branches
from .config import Config, ConfigError
from .pipeline.orchestrator import (
    run_post_only,
    run_review_only,
    run_full,
)


# ---------------------------------------------------------------------------
# Helpers
# ---------------------------------------------------------------------------


def _build_common_parser() -> argparse.ArgumentParser:
    p = argparse.ArgumentParser(add_help=False)
    p.add_argument("--org", dest="ado_org", help="ADO org short name (env: ADO_ORG)")
    p.add_argument("--project", dest="ado_project", help="ADO project (env: ADO_PROJECT)")
    p.add_argument("--repo", dest="ado_repo_id", help="ADO repo id or name (env: ADO_REPO_ID)")
    p.add_argument("--pr", dest="pr_id", help="PR id or full URL (env: PR_ID, PR_URL)")
    p.add_argument("--pr-url", dest="pr_url", help="Full PR URL (env: PR_URL)")
    p.add_argument("--source-branch", dest="source_branch", help="Source branch")
    p.add_argument("--target-branch", dest="target_branch", help="Target branch")
    p.add_argument("--ado-token", dest="ado_token", help="ADO bearer token (env: ADO_AUTH_TOKEN)")
    p.add_argument("--pi-model", dest="pi_model", help="Pi model pattern (env: PI_MODEL)")
    p.add_argument(
        "--language", dest="review_language", help="Comment language (env: REVIEW_LANGUAGE)"
    )
    p.add_argument(
        "--review-artifact-dir",
        dest="review_artifact_dir",
        help="Override the artifact directory",
    )
    p.add_argument(
        "--review-run-id", dest="review_run_id", help="Override the run id (deterministic output)"
    )
    p.add_argument(
        "--dry-run", dest="dry_run", action="store_true", default=None,
        help="Generate findings without posting (env: DRY_RUN)",
    )
    p.add_argument(
        "--no-dry-run", dest="dry_run", action="store_false", default=None,
        help="Force posting (overrides --dry-run and DRY_RUN env)",
    )
    p.add_argument(
        "--force-review", dest="force_review", action="store_true", default=None,
        help="Review even when skip policy would skip (drafts, closed, etc.)",
    )
    p.add_argument(
        "--force-full-review", dest="force_full_review", action="store_true", default=None,
        help="Ignore review history and run a complete review",
    )
    # Pi session reuse (Phases A + E of the token-savings plan).
    p.add_argument(
        "--pi-session-id", dest="pi_session_id",
        help="Pi session id (default: pr-<pr_id>-review-<run_id>)",
    )
    p.add_argument(
        "--no-pi-session", dest="pi_session_enabled", action="store_false", default=None,
        help="Disable Pi session reuse (use --no-session; deterministic reruns)",
    )
    p.add_argument(
        "--pi-session-clear", dest="pi_session_clear", action="store_true", default=None,
        help="Start a fresh session under the same id (clear prior state)",
    )
    p.add_argument(
        "--fast-review", dest="fast_review", action="store_true", default=None,
        help="Alias for --reasoning-engine single_pi (env: FAST_REVIEW)",
    )
    p.add_argument(
        "--reasoning-engine", dest="reasoning_engine",
        help="Reasoning engine: multi_stage|single_pi (env: REASONING_ENGINE)",
    )
    return p


def _apply_common(cfg: Config, args: argparse.Namespace) -> Config:
    """Return a copy of ``cfg`` with any non-None CLI args applied."""
    overrides: dict[str, Any] = {}
    for field in (
        "ado_org", "ado_project", "ado_repo_id", "pr_id", "pr_url",
        "source_branch", "target_branch", "ado_token", "pi_model",
        "review_language", "review_artifact_dir", "review_run_id",
        "pi_session_id", "reasoning_engine",
    ):
        v = getattr(args, field, None)
        if v not in (None, ""):
            overrides[field] = v
    for field in (
        "dry_run", "force_review", "force_full_review", "pi_session_enabled",
        "pi_session_clear", "fast_review",
    ):
        v = getattr(args, field, None)
        if v is not None:
            overrides[field] = v
    if "pr_id" in overrides and not str(overrides["pr_id"]).isdigit():
        # Allow ``--pr https://...`` shape.
        url = overrides.pop("pr_id")
        overrides.setdefault("pr_url", url)
    return cfg.with_overrides(**overrides) if overrides else cfg


def _build_config(args: argparse.Namespace) -> Config:
    """Build a :class:`Config` from CLI + env, with alias resolution."""
    cli: dict[str, Any] = {}
    for field in (
        "ado_org", "ado_project", "ado_repo_id", "pr_id", "pr_url",
        "source_branch", "target_branch", "ado_token", "pi_model",
        "review_language", "review_artifact_dir", "review_run_id",
        "pi_session_id", "pi_session_enabled", "pi_session_clear",
        "dry_run", "force_review", "force_full_review", "fast_review", "reasoning_engine",
    ):
        v = getattr(args, field, None)
        if v not in (None, ""):
            cli[field] = v
    try:
        return Config.from_sources(cli)
    except ConfigError as exc:
        _emit_config_error(exc, command=getattr(args, "_command", "review"))
        raise SystemExit(2)


def _emit_config_error(exc: ConfigError, *, command: str) -> None:
    """Print a friendly error message to stderr."""
    print(f"[review][ERROR] {exc}", file=sys.stderr)
    print(f"  Required by command: {command}", file=sys.stderr)


# ---------------------------------------------------------------------------
# Subcommands
# ---------------------------------------------------------------------------


def cmd_review(args: argparse.Namespace) -> int:
    cfg = _build_config(args)
    problems = cfg.validate_for_command("review")
    if problems:
        for p in problems:
            print(f"[review][ERROR] {p}", file=sys.stderr)
        return 2
    if not args.no_post and not args.output:
        outcome = run_full(cfg)
        return outcome.exit_code
    outcome = run_review_only(cfg, output=args.output)
    return outcome.exit_code


def cmd_post(args: argparse.Namespace) -> int:
    cfg = _build_config(args)
    problems = cfg.validate_for_command("post")
    if problems:
        for p in problems:
            print(f"[review][ERROR] {p}", file=sys.stderr)
        return 2
    if not args.input:
        print("[review][ERROR] --input is required for `post`", file=sys.stderr)
        return 2
    outcome = run_post_only(cfg, input_path=Path(args.input))
    return outcome.exit_code


def cmd_open_prs(args: argparse.Namespace) -> int:
    """Always fail with a clear pointer to the PowerShell entrypoint.

    ReviewForge is designed to run one container per pull request, not
    one container processing many. The PowerShell script
    ``run-open-prs.ps1`` is the only supported entrypoint for batch
    processing: it discovers active PRs and spawns a fresh container for
    each. This Python command exists only to fail fast with a useful
    message if someone tries to use it.
    """
    print(
        "[review][ERROR] 'open-prs' is not supported in the Python CLI.\n"
        "  ReviewForge runs one container per pull request. For batch\n"
        "  processing, use the PowerShell script at the repo root:\n"
        "    ./run-open-prs.ps1 [-Organization <url>] [-Projects <names>] ...",
        file=sys.stderr,
    )
    return 2


def cmd_discover(args: argparse.Namespace) -> int:
    """Discover active pull requests for a project. Emits JSON on stdout.

    Output shape::

        [
            {
                "pullRequestId": 123,
                "title": "...",
                "sourceRefName": "refs/heads/feature/x",
                "targetRefName": "refs/heads/main",
                "isDraft": false,
                "status": "active",
                "project": "Payments",
                "repositoryId": "...",
                "reviewers": [...],
                "createdBy": {...}
            },
            ...
        ]

    Used by ``run-open-prs.ps1`` to discover PRs without calling the
    Azure CLI directly.
    """
    from .ado.client import list_active_pull_requests
    from .config import ConfigError

    cli = {
        "ado_org": args.ado_org,
        "ado_project": args.ado_project,
        "pr_id": "",  # not needed for list
    }
    if args.ado_token:
        cli["ado_token"] = args.ado_token
    try:
        cfg = Config.from_sources(cli)
    except ConfigError as exc:
        print(f"[review][ERROR] {exc}", file=sys.stderr)
        return 2
    if not cfg.ado_token:
        print(
            "[review][ERROR] Missing required config: ADO_AUTH_TOKEN (aliases: ADO_MCP_AUTH_TOKEN, ADO_API_KEY).",
            file=sys.stderr,
        )
        return 2

    target_branches = [b.strip() for b in (args.target_branches or "").split(",") if b.strip()] or None
    try:
        prs = list_active_pull_requests(
            cfg,
            project=args.ado_project or cfg.ado_project,
            target_branches=target_branches,
            max_results=args.max or 0,
        )
    except SystemExit as exc:
        print(f"[review][ERROR] {exc}", file=sys.stderr)
        return 2
    print(json.dumps(prs, ensure_ascii=False, indent=2))
    return 0


def cmd_validate_config(args: argparse.Namespace) -> int:
    try:
        cfg = _build_config(args)
    except SystemExit as exc:
        # _build_config raises SystemExit(2) on hard config errors so that
        # other commands fail fast. ``validate-config`` is the one place
        # where we want a clean exit code (1) and a captured error, so
        # swallow the SystemExit here and let the caller see rc=1.
        return int(exc.code) if isinstance(exc.code, int) and exc.code in (0, 1) else 1
    command = getattr(args, "_command", "review")
    problems = cfg.validate_for_command(command)
    if problems:
        for p in problems:
            print(f"[review][ERROR] {p}", file=sys.stderr)
        return 1
    if command in {"review", "post"}:
        try:
            cfg.validate_files()
        except ConfigError as exc:
            print(f"[review][ERROR] {exc}", file=sys.stderr)
            return 1
    print(f"[review] configuration for command '{command}' is valid")
    print(f"  org:     {cfg.ado_org}")
    print(f"  project: {cfg.ado_project}")
    print(f"  repo:    {cfg.ado_repo_id}")
    print(f"  pr:      {cfg.pr_id or '(none)'}")
    print(f"  model:   {cfg.pi_model}")
    print(f"  language:{cfg.review_language}")
    return 0


# ---------------------------------------------------------------------------
# Parser
# ---------------------------------------------------------------------------


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(
        prog="reviewforge",
        description="Azure DevOps ReviewForge",
    )
    sub = parser.add_subparsers(dest="command", required=False)

    common = _build_common_parser()

    review = sub.add_parser(
        "review",
        parents=[common],
        help="Generate findings for a PR (and post them by default).",
    )
    review.add_argument(
        "--output", help="Optional output path for final-findings.json"
    )
    review.add_argument(
        "--no-post",
        dest="no_post",
        action="store_true",
        help="Generate findings only; do not post to ADO",
    )
    review.set_defaults(func=cmd_review, _command="review")

    post = sub.add_parser(
        "post",
        parents=[common],
        help="Post a previously generated review to ADO.",
    )
    post.add_argument(
        "--input", help="Path to the review JSON to post (required)"
    )
    post.set_defaults(func=cmd_post, _command="post")

    open_prs = sub.add_parser(
        "open-prs",
        parents=[common],
        help=(
            "Unsupported: use ./run-open-prs.ps1 for batch processing. "
            "ReviewForge runs one container per PR."
        ),
    )
    open_prs.set_defaults(func=cmd_open_prs, _command="open-prs")

    validate = sub.add_parser(
        "validate-config",
        parents=[common],
        help="Validate the configuration and exit.",
    )
    validate.set_defaults(func=cmd_validate_config, _command="review")

    # ``discover`` has a different arg surface than the PR-scoped commands,
    # so it does not inherit from the common parent parser.
    discover = sub.add_parser(
        "discover",
        help="Discover active pull requests for a project (emits JSON).",
    )
    discover.add_argument(
        "--org", dest="ado_org", help="ADO org short name (env: ADO_ORG)",
    )
    discover.add_argument(
        "--project", dest="ado_project", required=True,
        help="ADO project name to scan (env: ADO_PROJECT)",
    )
    discover.add_argument(
        "--ado-token", dest="ado_token",
        help="ADO bearer token (env: ADO_AUTH_TOKEN / ADO_MCP_AUTH_TOKEN / ADO_API_KEY)",
    )
    discover.add_argument(
        "--target-branches", dest="target_branches",
        help="Comma-separated list of target branch names (ref names OK)",
    )
    discover.add_argument(
        "--max", type=int, default=0, help="Cap the number of results (0 = no cap)",
    )
    discover.set_defaults(func=cmd_discover, _command="discover")

    # Legacy: a bare ``--pr`` invocation runs the full review pipeline.
    parser.set_defaults(func=cmd_review, _command="review", no_post=False)
    return parser


def main(argv: Sequence[str] | None = None) -> int:
    argv = list(sys.argv[1:] if argv is None else argv)
    # Default to ``review`` when no subcommand is given. The container
    # ENTRYPOINT invokes this entrypoint with no args, and
    # ``python -m reviewforge`` (no subcommand) should also land
    # on the primary use case. Callers wanting help should pass ``-h``.
    if not argv:
        argv = ["review"]
    parser = build_parser()
    args = parser.parse_args(argv)
    func = getattr(args, "func", None)
    if func is None:
        parser.print_help(sys.stderr)
        return 1
    return int(func(args))


__all__ = ["build_parser", "main"]
