"""Command-line interface for the auto-pr-reviewer.

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
    return p


def _apply_common(cfg: Config, args: argparse.Namespace) -> Config:
    """Return a copy of ``cfg`` with any non-None CLI args applied."""
    overrides: dict[str, Any] = {}
    for field in (
        "ado_org", "ado_project", "ado_repo_id", "pr_id", "pr_url",
        "source_branch", "target_branch", "ado_token", "pi_model",
        "review_language", "review_artifact_dir", "review_run_id",
    ):
        v = getattr(args, field, None)
        if v not in (None, ""):
            overrides[field] = v
    for field in ("dry_run", "force_review"):
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
        "dry_run", "force_review",
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

    The review bot is designed to run one container per pull request, not
    one container processing many. The PowerShell script
    ``run-open-prs.ps1`` is the only supported entrypoint for batch
    processing: it discovers active PRs and spawns a fresh container for
    each. This Python command exists only to fail fast with a useful
    message if someone tries to use it.
    """
    print(
        "[review][ERROR] 'open-prs' is not supported in the Python CLI.\n"
        "  The review bot runs one container per pull request. For batch\n"
        "  processing, use the PowerShell script at the repo root:\n"
        "    ./run-open-prs.ps1 [-Organization <url>] [-Projects <names>] ...",
        file=sys.stderr,
    )
    return 2


def cmd_validate_config(args: argparse.Namespace) -> int:
    cfg = _build_config(args)
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
        prog="auto-pr-reviewer",
        description="Azure DevOps PR review bot",
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
            "The review bot runs one container per PR."
        ),
    )
    open_prs.set_defaults(func=cmd_open_prs, _command="open-prs")

    validate = sub.add_parser(
        "validate-config",
        parents=[common],
        help="Validate the configuration and exit.",
    )
    validate.set_defaults(func=cmd_validate_config, _command="review")

    # Legacy: a bare ``--pr`` invocation runs the full review pipeline.
    parser.set_defaults(func=cmd_review, _command="review", no_post=False)
    return parser


def main(argv: Sequence[str] | None = None) -> int:
    argv = list(sys.argv[1:] if argv is None else argv)
    parser = build_parser()
    if not argv:
        parser.print_help(sys.stderr)
        return 1
    args = parser.parse_args(argv)
    func = getattr(args, "func", None)
    if func is None:
        parser.print_help(sys.stderr)
        return 1
    return int(func(args))


__all__ = ["build_parser", "main"]
