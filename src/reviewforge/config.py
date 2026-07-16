"""Configuration loading, validation, and CLI/env alias resolution.

Configuration is read from CLI flags (highest priority), then environment
variables / ``.env``, with alias resolution for keys that have multiple names.

The :class:`Config` dataclass is the immutable configuration consumed by the
review pipeline. :meth:`Config.from_sources` and :meth:`Config.from_env` are the
two constructors: ``from_env`` is the legacy entrypoint used by tests; new code
should prefer :meth:`Config.from_sources` with explicit CLI overrides.
"""
from __future__ import annotations

from dataclasses import dataclass, field, replace
from pathlib import Path
from typing import Any, Callable, Mapping
import os
import re
import sys

# ---------------------------------------------------------------------------
# Generic helpers
# ---------------------------------------------------------------------------


def is_true(value: str | None) -> bool:
    return (value or "").lower() in {"1", "true", "yes", "on"}


def _coerce_bool(value: Any, default: bool, *, env_value: str | None = None) -> bool:
    """Coerce a CLI/env value to a bool with a sensible default.

    - ``True`` / ``False`` pass through.
    - Strings parse via :func:`is_true`.
    - ``None`` falls back to ``env_value`` (string), then to ``default``.
    """
    if isinstance(value, bool):
        return value
    if value is not None:
        return is_true(str(value))
    if env_value is not None:
        return is_true(env_value)
    return default


def require_uint(name: str, value: str) -> int:
    if not re.fullmatch(r"\d+", value or ""):
        raise ConfigError(f"{name} must be a non-negative integer, got: {value!r}")
    return int(value)


def env(name: str, default: str | None = None) -> str:
    """Read an env var or fail with a clear error message."""
    value = os.getenv(name)
    if value is None or value == "":
        if default is None:
            raise ConfigError(f"{name} required")
        return default
    return value


def parse_dotenv(path: str | os.PathLike[str]) -> dict[str, str]:
    """Parse a ``.env`` file into a flat dict of string→string.

    Format follows the de-facto convention used by Docker / dotenv / pip:

    * Blank lines and ``#`` comments are skipped.
    * Each non-empty line is ``KEY=VALUE`` (with optional surrounding
      whitespace).
    * Values may be wrapped in matching ``"`` or ``'`` quotes; the quotes
      are stripped. (No escape processing — keep it simple.)
    * Lines that do not match ``KEY=VALUE`` are silently ignored.
    """
    out: dict[str, str] = {}
    p = Path(path)
    if not p.exists():
        return out
    for raw in p.read_text(encoding="utf-8", errors="replace").splitlines():
        line = raw.strip()
        if not line or line.startswith("#"):
            continue
        m = re.match(r"^([A-Za-z_][A-Za-z0-9_]*)\s*=\s*(.*)$", line)
        if not m:
            continue
        key, value = m.group(1), m.group(2).strip()
        if len(value) >= 2 and value[0] == value[-1] and value[0] in {"'", '"'}:
            value = value[1:-1]
        out[key] = value
    return out


class ConfigError(ValueError):
    """Raised when configuration is missing or invalid.

    The CLI layer translates this into a friendly, actionable error message
    identifying the missing key, the command that needs it, and how to set it.
    """


# ---------------------------------------------------------------------------
# Aliases — multiple env names map to the same logical config key
# ---------------------------------------------------------------------------

#: Mapping of logical key → tuple of accepted env var names. The first hit wins.
_ENV_ALIASES: dict[str, tuple[str, ...]] = {
    "ado_token": ("SYSTEM_ACCESSTOKEN", "ADO_AUTH_TOKEN", "ADO_MCP_AUTH_TOKEN", "ADO_API_KEY"),
    "ado_org": ("ADO_ORG",),
    "ado_project": ("ADO_PROJECT",),
    "ado_repo_id": ("ADO_REPO_ID",),
    "pr_id": ("PR_ID", "PR_URL"),
    "source_branch": ("SOURCE_BRANCH",),
    "target_branch": ("TARGET_BRANCH",),
    "review_language": ("REVIEW_LANGUAGE",),
    "pi_model": ("PI_MODEL",),
    "image": ("IMAGE_NAME", "IMAGE"),
}


def _read_env_with_aliases(key: str, env: Mapping[str, str] | None = None) -> str | None:
    """Return the first non-empty value among the aliases for ``key``.

    If ``env`` is provided, it is searched instead of ``os.environ`` (used in
    tests). Falls back to ``key.upper()`` for keys that have no aliases.
    """
    src: Callable[[str], str | None]
    if env is None:
        src = os.getenv
    else:
        src = env.get
    for name in _ENV_ALIASES.get(key, (key.upper(),)):
        value = src(name)
        if value:
            return value
    return None


# ---------------------------------------------------------------------------
# Config dataclass
# ---------------------------------------------------------------------------


@dataclass(frozen=True)
class Config:
    ado_org: str
    ado_project: str
    ado_repo_id: str
    pr_id: str
    ado_token: str
    source_branch: str
    target_branch: str
    workspace: Path
    clone_root: Path
    review_language: str
    review_prompt_path: Path
    intent_prompt_path: Path
    context_plan_prompt_path: Path
    context_digest_prompt_path: Path
    verify_prompt_path: Path
    severity_prompt_path: Path
    standards_path: Path
    pi_model: str
    max_diff_bytes: int
    chunk_trigger_diff_bytes: int
    disable_chunk_review: bool
    pi_timeout_secs: int
    dry_run: bool
    include_work_items: bool
    include_existing_comments: bool
    verify_findings: bool
    force_review: bool
    review_target_branches: str
    review_artifact_dir: str | None
    review_artifact_root: Path
    review_run_id: str | None
    #: Optional pre-resolved PR URL string. When set, ``pr_id`` was derived from it.
    pr_url: str | None = field(default=None, compare=False)
    # --- Posting & severity thresholds (used by the posting CLI / stage) ---
    #: ADO posting: minimum severity to actually post. ``none`` disables.
    post_min_severity: str = field(default="none", compare=False)
    #: ADO posting: drop findings with ``confidence == "low"`` when true.
    drop_low_confidence: bool = field(default=False, compare=False)
    #: ADO posting: comma-separated severities that require evidence.
    require_context_for: str = field(default="", compare=False)
    #: ADO posting: hard cap on number of findings posted. ``None`` = no cap.
    max_findings: int | None = field(default=None, compare=False)
    #: ADO voting: severity at-or-above which to vote ``waiting for author``.
    vote_waiting_on: str = field(default="none", compare=False)
    #: Exit non-zero when findings at-or-above this severity are present.
    fail_on: str = field(default="none", compare=False)
    # --- Context collection ---
    #: Max lines to read from a single file referenced by the context plan.
    context_file_max_lines: int = field(default=260, compare=False)
    #: Max search matches per ``searches_to_run`` query.
    context_search_max_matches: int = field(default=40, compare=False)
    #: Max worker threads used by context collection.
    collect_context_workers: int = field(default=8, compare=False)
    # --- AC coverage LLM re-check (optional second-pass) ------------
    #: When ``True``, run an LLM re-check on ACs flagged uncovered by
    #: the deterministic string search. Default off to keep cost down.
    ac_coverage_llm: bool = field(default=False, compare=False)
    #: Maximum number of uncovered ACs to send to the LLM per run.
    ac_coverage_llm_max_acs: int = field(default=10, compare=False)
    #: System prompt for the AC coverage LLM re-check.
    ac_coverage_prompt_path: Path = field(default=Path("/app/prompts/ac-coverage.md"), compare=False)
    # --- Pi session reuse (Phases A + E) -------------------------------
    #: When ``True`` (default), the runner uses ``--session`` to keep state
    #: between stages and chunks. Disable for deterministic reruns or when
    #: the underlying Pi session is corrupted.
    pi_session_enabled: bool = field(default=True, compare=False)
    #: When ``True``, the runner starts a fresh session, ignoring any
    #: prior state for the same session id.
    pi_session_clear: bool = field(default=False, compare=False)
    #: Session id used by Pi. Defaults to ``pr-{pr_id}-review-{run_id}``
    #: so the same PR reuses state across reruns.
    pi_session_id: str | None = field(default=None, compare=False)

    # ------------------------------------------------------------------ env --

    @classmethod
    def from_env(cls) -> "Config":
        """Legacy constructor: build a :class:`Config` from environment variables.

        Mirrors the behavior of the original ``src/reviewforge/config.py`` for tests
        and existing PowerShell wrappers. New callers should use
        :meth:`from_sources` for CLI-override semantics.
        """
        token = _read_env_with_aliases("ado_token")
        if not token:
            raise ConfigError(
                "Missing required config: ADO_AUTH_TOKEN (aliases: ADO_MCP_AUTH_TOKEN, ADO_API_KEY). "
                "Set it in .env, as an environment variable, or pass --ado-token."
            )

        review_artifact_dir = os.getenv("REVIEW_ARTIFACT_DIR") or None
        review_artifact_root = Path(os.getenv("REVIEW_ARTIFACT_ROOT", "/workspace/artifacts"))
        review_run_id = os.getenv("REVIEW_RUN_ID") or None
        if not os.getenv("CHUNK_TRIGGER_DIFF_BYTES"):
            os.environ["CHUNK_TRIGGER_DIFF_BYTES"] = str(int(os.getenv("MAX_DIFF_BYTES", "200000")))

        # Pi session controls.
        pi_session_id = os.getenv("PI_SESSION_ID") or None
        pi_session_enabled = (os.getenv("PI_SESSION_ENABLED", "1").lower() not in {"0", "false", "no", "off"})
        pi_session_clear = (os.getenv("PI_SESSION_CLEAR", "0").lower() in {"1", "true", "yes", "on"})

        # Posting thresholds (used by reviewforge.ado.cli legacy posting).
        post_min_severity = os.getenv("POST_MIN_SEVERITY", "none")
        drop_low_confidence = is_true(os.getenv("DROP_LOW_CONFIDENCE"))
        require_context_for = os.getenv("REQUIRE_CONTEXT_FOR", "")
        max_findings_raw = os.getenv("MAX_FINDINGS")
        if max_findings_raw is None or max_findings_raw == "":
            max_findings = None
        else:
            try:
                max_findings = int(max_findings_raw)
            except ValueError:
                max_findings = None
        vote_waiting_on = os.getenv("VOTE_WAITING_ON", "none")
        fail_on = os.getenv("FAIL_ON", "none")

        # Context collection caps.
        context_file_max_lines_raw = os.getenv("CONTEXT_FILE_MAX_LINES", "260")
        context_search_max_matches_raw = os.getenv("CONTEXT_SEARCH_MAX_MATCHES", "40")
        collect_context_workers_raw = os.getenv("COLLECT_CONTEXT_WORKERS", "8")
        try:
            context_file_max_lines = int(context_file_max_lines_raw)
        except ValueError:
            context_file_max_lines = 260
        try:
            context_search_max_matches = int(context_search_max_matches_raw)
        except ValueError:
            context_search_max_matches = 40
        try:
            collect_context_workers = int(collect_context_workers_raw)
        except ValueError:
            collect_context_workers = 8

        # AC coverage LLM re-check.
        ac_coverage_llm = is_true(os.getenv("AC_COVERAGE_LLM"))
        ac_coverage_llm_max_acs = require_uint(
            "AC_COVERAGE_LLM_MAX_ACS", os.getenv("AC_COVERAGE_LLM_MAX_ACS", "10")
        )
        ac_coverage_prompt_path = _resolve_prompt_path(
            "AC_COVERAGE_PROMPT_PATH", "/app/prompts/ac-coverage.md"
        )

        cfg = cls(
            ado_org=os.getenv("ADO_ORG", ""),
            ado_project=os.getenv("ADO_PROJECT", ""),
            ado_repo_id=os.getenv("ADO_REPO_ID", ""),
            pr_id=os.getenv("PR_ID", "") or _extract_pr_id_from_url(os.getenv("PR_URL", "")),
            ado_token=token,
            source_branch=os.getenv("SOURCE_BRANCH", ""),
            target_branch=os.getenv("TARGET_BRANCH", ""),
            workspace=Path(os.getenv("WORKSPACE", "/workspace")),
            clone_root=Path(os.getenv("CLONE_ROOT", "/workspace/repo")),
            review_language=os.getenv("REVIEW_LANGUAGE", "English"),
            review_prompt_path=_resolve_prompt_path("REVIEW_PROMPT_PATH", "/app/prompts/review-system.md"),
            intent_prompt_path=_resolve_prompt_path("INTENT_PROMPT_PATH", "/app/prompts/intent.md"),
            context_plan_prompt_path=_resolve_prompt_path("CONTEXT_PLAN_PROMPT_PATH", "/app/prompts/context-plan.md"),
            context_digest_prompt_path=_resolve_prompt_path("CONTEXT_DIGEST_PROMPT_PATH", "/app/prompts/context-digest.md"),
            verify_prompt_path=_resolve_prompt_path("VERIFY_PROMPT_PATH", "/app/prompts/verify-findings.md"),
            severity_prompt_path=_resolve_prompt_path("SEVERITY_PROMPT_PATH", "/app/prompts/severity.md"),
            standards_path=Path(os.getenv("REVIEW_STANDARDS_PATH", "/app/standards/clean-code.md")),
            pi_model=os.getenv("PI_MODEL", "openai/gpt-5.5"),
            max_diff_bytes=require_uint("MAX_DIFF_BYTES", os.getenv("MAX_DIFF_BYTES", "200000")),
            chunk_trigger_diff_bytes=require_uint(
                "CHUNK_TRIGGER_DIFF_BYTES", os.getenv("CHUNK_TRIGGER_DIFF_BYTES", "200000")
            ),
            disable_chunk_review=is_true(os.getenv("DISABLE_CHUNK_REVIEW")),
            pi_timeout_secs=require_uint("PI_TIMEOUT_SECS", os.getenv("PI_TIMEOUT_SECS", "600")),
            dry_run=is_true(os.getenv("DRY_RUN")),
            include_work_items=is_true(os.getenv("INCLUDE_WORK_ITEMS", "1")),
            include_existing_comments=is_true(os.getenv("INCLUDE_EXISTING_COMMENTS", "1")),
            verify_findings=is_true(os.getenv("VERIFY_FINDINGS", "1")),
            force_review=is_true(os.getenv("FORCE_REVIEW")),
            review_target_branches=os.getenv("REVIEW_TARGET_BRANCHES", ""),
            review_artifact_dir=review_artifact_dir,
            review_artifact_root=review_artifact_root,
            review_run_id=review_run_id,
            pr_url=os.getenv("PR_URL") or None,
            pi_session_id=pi_session_id,
            pi_session_enabled=pi_session_enabled,
            pi_session_clear=pi_session_clear,
            post_min_severity=post_min_severity,
            drop_low_confidence=drop_low_confidence,
            require_context_for=require_context_for,
            max_findings=max_findings,
            vote_waiting_on=vote_waiting_on,
            fail_on=fail_on,
            context_file_max_lines=context_file_max_lines,
            context_search_max_matches=context_search_max_matches,
            collect_context_workers=collect_context_workers,
            ac_coverage_llm=ac_coverage_llm,
            ac_coverage_llm_max_acs=ac_coverage_llm_max_acs,
            ac_coverage_prompt_path=ac_coverage_prompt_path,
        )
        return cfg

    # -------------------------------------------------------------- sources --

    @classmethod
    def from_sources(
        cls,
        cli: Mapping[str, Any] | None = None,
        *,
        env: Mapping[str, str] | None = None,
    ) -> "Config":
        """Build a config from CLI overrides layered on top of env vars.

        ``cli`` may provide any subset of the constructor fields. The values
        are coerced to the right type and take precedence over env. ``env`` is
        normally ``os.environ`` and can be overridden in tests.
        """
        env_map = env if env is not None else os.environ
        cli_map = dict(cli or {})
        return _build_from_sources(cls, cli_map, env_map)

    @classmethod
    def from_env_file(
        cls,
        path: str | os.PathLike[str] | None = None,
        cli: Mapping[str, Any] | None = None,
    ) -> "Config":
        """Build a config by merging a ``.env`` file with the process env.

        Precedence: ``cli`` > process env > ``.env`` file. The ``.env`` file
        is the *lowest* layer; the existing process env (already in
        ``os.environ``) takes precedence because PowerShell forwards the
        live env first, then the file is the fallback.

        Pass ``path=None`` to read ``.env`` in the current directory; pass
        a different path to point at any file.
        """
        if path is None:
            path = Path(".env")
        file_values = parse_dotenv(path)
        # Merge: process env overrides the file. Then call from_sources.
        merged: dict[str, str] = {**file_values, **os.environ}
        return cls.from_sources(cli, env=merged)

    # -------------------------------------------------------- validation --

    def validate_files(self) -> None:
        """Ensure all required prompt/standards files exist."""
        paths = [
            self.review_prompt_path,
            self.intent_prompt_path,
            self.context_plan_prompt_path,
            self.context_digest_prompt_path,
            self.verify_prompt_path,
            self.severity_prompt_path,
            self.standards_path,
        ]
        if self.ac_coverage_llm:
            paths.append(self.ac_coverage_prompt_path)
        for path in paths:
            if not path.exists():
                raise ConfigError(f"Required file not found: {path}")

    def validate_for_command(self, command: str) -> list[str]:
        """Return a list of human-readable error messages for ``command``.

        Commands have different requirements (e.g. ``open-prs`` does not need
        ``pr_id``; ``review`` does). Returns ``[]`` when valid.
        """
        problems: list[str] = []
        # Universal: token + org + project + repo are required for any ADO call.
        if not self.ado_token:
            problems.append(self._missing("ADO_AUTH_TOKEN", command, "--ado-token"))
        if not self.ado_org:
            problems.append(self._missing("ADO_ORG", command, "--ado-org"))
        if not self.ado_project:
            problems.append(self._missing("ADO_PROJECT", command, "--ado-project"))
        if not self.ado_repo_id:
            problems.append(self._missing("ADO_REPO_ID", command, "--ado-repo-id"))

        if command in {"review", "post", "fetch-context"}:
            if not self.pr_id:
                problems.append(self._missing("PR_ID (or PR_URL)", command, "--pr"))
        if command in {"post"}:
            # The post path needs branches resolved or fetched.
            if not self.source_branch and not self.target_branch:
                # Not fatal — resolve_branches() can fetch from the API — but warn.
                pass
        if command in {"review"}:
            if not self.ado_token:
                # already covered
                pass
        return problems

    @staticmethod
    def _missing(name: str, command: str, flag: str) -> str:
        return (
            f"Missing required config: {name}\n"
            f"  Required by command: {command}\n"
            f"  Set it in .env, as an environment variable, or pass {flag}."
        )

    # ------------------------------------------------------------ mutate --

    def with_overrides(self, **kwargs: Any) -> "Config":
        """Return a new :class:`Config` with the given fields replaced."""
        return replace(self, **kwargs)


# ---------------------------------------------------------------------------
# Internal builders
# ---------------------------------------------------------------------------


def _resolve_prompt_path(env_name: str, default: str) -> Path:
    raw = os.getenv(env_name)
    if raw:
        return Path(raw)
    return Path(default)


def _extract_pr_id_from_url(url: str) -> str:
    if not url:
        return ""
    m = re.search(r"/pullrequest/(\d+)", url)
    return m.group(1) if m else ""


def _coerce_cli_value(field_name: str, value: Any) -> Any:
    """Best-effort coercion of CLI strings to the right type for the dataclass."""
    if value is None:
        return None
    if isinstance(value, bool):
        return value
    if isinstance(value, (int, float)):
        return value
    if not isinstance(value, str):
        return value
    raw = value.strip()
    if field_name in {"dry_run", "disable_chunk_review", "include_work_items",
                      "include_existing_comments", "verify_findings", "force_review",
                      "ac_coverage_llm"}:
        return is_true(raw)
    if field_name in {"max_diff_bytes", "chunk_trigger_diff_bytes", "pi_timeout_secs",
                      "ac_coverage_llm_max_acs"}:
        return require_uint(field_name.upper(), raw)
    if field_name.endswith("_path") or field_name in {"workspace", "clone_root",
                                                      "review_artifact_root", "standards_path"}:
        return Path(raw)
    return raw


def _build_from_sources(
    cls: type[Config],
    cli: dict[str, Any],
    env: Mapping[str, str],
) -> Config:
    """Build a :class:`Config` from CLI + env, with alias resolution."""

    def cli_or_env(field: str, key: str, default: str = "") -> str:
        if field in cli and cli[field] not in (None, ""):
            return str(cli[field])
        v = env.get(key)
        if v:
            return v
        return default

    # Token: CLI wins; else try each alias in order.
    token = ""
    if cli.get("ado_token"):
        token = str(cli["ado_token"])
    else:
        for alias in _ENV_ALIASES["ado_token"]:
            v = env.get(alias)
            if v:
                token = v
                break
    if not token:
        raise ConfigError(
            "Missing required config: ADO_AUTH_TOKEN (aliases: ADO_MCP_AUTH_TOKEN, ADO_API_KEY). "
            "Set it in .env, as an environment variable, or pass --ado-token."
        )

    review_artifact_dir = cli_or_env("review_artifact_dir", "REVIEW_ARTIFACT_DIR") or None
    review_artifact_root = cli_or_env("review_artifact_root", "REVIEW_ARTIFACT_ROOT") or "/workspace/artifacts"
    review_run_id = cli_or_env("review_run_id", "REVIEW_RUN_ID") or None

    # Pi session controls (Phase A + E).
    pi_session_id = cli.get("pi_session_id") or os.getenv("PI_SESSION_ID") or None
    pi_session_enabled = _coerce_bool(cli.get("pi_session_enabled"), default=True,
                                       env_value=os.getenv("PI_SESSION_ENABLED"))
    pi_session_clear = _coerce_bool(cli.get("pi_session_clear"), default=False,
                                     env_value=os.getenv("PI_SESSION_CLEAR"))

    # PR id: prefer explicit CLI ``--pr``; fall back to PR_ID env, then PR_URL.
    pr_id = cli_or_env("pr_id", "PR_ID")
    pr_url = cli_or_env("pr_url", "PR_URL") or None
    # Allow the URL to populate org/project/repo/pr_id when no other source has them.
    url_org = url_proj = url_repo = url_pr_id = ""
    if pr_url:
        try:
            from .ado.client import parse_pr_url
            url_org, url_proj, url_repo, url_pr_id = parse_pr_url(pr_url)
        except SystemExit:
            url_org = url_proj = url_repo = url_pr_id = ""
    if not pr_id and url_pr_id:
        pr_id = url_pr_id
    pr_id = pr_id or ""
    org_value = cli_or_env("ado_org", "ADO_ORG") or url_org
    project_value = cli_or_env("ado_project", "ADO_PROJECT") or url_proj
    repo_value = cli_or_env("ado_repo_id", "ADO_REPO_ID") or url_repo

    max_diff_bytes = require_uint("MAX_DIFF_BYTES", cli_or_env("max_diff_bytes", "MAX_DIFF_BYTES", "200000"))
    chunk_trigger = cli_or_env("chunk_trigger_diff_bytes", "CHUNK_TRIGGER_DIFF_BYTES")
    if not chunk_trigger:
        chunk_trigger = str(max_diff_bytes)
    chunk_trigger_diff_bytes = require_uint("CHUNK_TRIGGER_DIFF_BYTES", chunk_trigger)
    pi_timeout = require_uint("PI_TIMEOUT_SECS", cli_or_env("pi_timeout_secs", "PI_TIMEOUT_SECS", "600"))
    context_file_max_lines = require_uint(
        "CONTEXT_FILE_MAX_LINES", cli_or_env("context_file_max_lines", "CONTEXT_FILE_MAX_LINES", "260")
    )
    context_search_max_matches = require_uint(
        "CONTEXT_SEARCH_MAX_MATCHES", cli_or_env("context_search_max_matches", "CONTEXT_SEARCH_MAX_MATCHES", "40")
    )
    collect_context_workers = require_uint(
        "COLLECT_CONTEXT_WORKERS", cli_or_env("collect_context_workers", "COLLECT_CONTEXT_WORKERS", "8")
    )
    ac_coverage_llm = is_true(cli_or_env("ac_coverage_llm", "AC_COVERAGE_LLM"))
    ac_coverage_llm_max_acs = require_uint(
        "AC_COVERAGE_LLM_MAX_ACS", cli_or_env("ac_coverage_llm_max_acs", "AC_COVERAGE_LLM_MAX_ACS", "10")
    )

    def to_path(value: str, default: str) -> Path:
        return Path(value) if value else Path(default)

    return cls(
        ado_org=org_value,
        ado_project=project_value,
        ado_repo_id=repo_value,
        pr_id=pr_id,
        ado_token=token,
        source_branch=cli_or_env("source_branch", "SOURCE_BRANCH"),
        target_branch=cli_or_env("target_branch", "TARGET_BRANCH"),
        workspace=to_path(cli_or_env("workspace", "WORKSPACE"), "/workspace"),
        clone_root=to_path(cli_or_env("clone_root", "CLONE_ROOT"), "/workspace/repo"),
        review_language=cli_or_env("review_language", "REVIEW_LANGUAGE", "English"),
        review_prompt_path=to_path(
            cli_or_env("review_prompt_path", "REVIEW_PROMPT_PATH"), "/app/prompts/review-system.md"
        ),
        intent_prompt_path=to_path(
            cli_or_env("intent_prompt_path", "INTENT_PROMPT_PATH"), "/app/prompts/intent.md"
        ),
        context_plan_prompt_path=to_path(
            cli_or_env("context_plan_prompt_path", "CONTEXT_PLAN_PROMPT_PATH"),
            "/app/prompts/context-plan.md",
        ),
        context_digest_prompt_path=to_path(
            cli_or_env("context_digest_prompt_path", "CONTEXT_DIGEST_PROMPT_PATH"),
            "/app/prompts/context-digest.md",
        ),
        verify_prompt_path=to_path(
            cli_or_env("verify_prompt_path", "VERIFY_PROMPT_PATH"), "/app/prompts/verify-findings.md"
        ),
        severity_prompt_path=to_path(
            cli_or_env("severity_prompt_path", "SEVERITY_PROMPT_PATH"), "/app/prompts/severity.md"
        ),
        standards_path=to_path(
            cli_or_env("standards_path", "REVIEW_STANDARDS_PATH"), "/app/standards/clean-code.md"
        ),
        pi_model=cli_or_env("pi_model", "PI_MODEL", "openai/gpt-5.5"),
        max_diff_bytes=max_diff_bytes,
        chunk_trigger_diff_bytes=chunk_trigger_diff_bytes,
        disable_chunk_review=is_true(cli_or_env("disable_chunk_review", "DISABLE_CHUNK_REVIEW")),
        pi_timeout_secs=pi_timeout,
        dry_run=is_true(cli_or_env("dry_run", "DRY_RUN")),
        include_work_items=is_true(cli_or_env("include_work_items", "INCLUDE_WORK_ITEMS", "1")),
        include_existing_comments=is_true(
            cli_or_env("include_existing_comments", "INCLUDE_EXISTING_COMMENTS", "1")
        ),
        verify_findings=is_true(cli_or_env("verify_findings", "VERIFY_FINDINGS", "1")),
        force_review=is_true(cli_or_env("force_review", "FORCE_REVIEW")),
        review_target_branches=cli_or_env("review_target_branches", "REVIEW_TARGET_BRANCHES"),
        review_artifact_dir=review_artifact_dir,
        review_artifact_root=Path(review_artifact_root),
        review_run_id=review_run_id,
        context_file_max_lines=context_file_max_lines,
        context_search_max_matches=context_search_max_matches,
        collect_context_workers=collect_context_workers,
        ac_coverage_llm=ac_coverage_llm,
        ac_coverage_llm_max_acs=ac_coverage_llm_max_acs,
        ac_coverage_prompt_path=to_path(
            cli_or_env("ac_coverage_prompt_path", "AC_COVERAGE_PROMPT_PATH"), "/app/prompts/ac-coverage.md"
        ),
        pr_url=pr_url,
        pi_session_id=pi_session_id,
        pi_session_enabled=pi_session_enabled,
        pi_session_clear=pi_session_clear,
    )


__all__ = [
    "Config",
    "ConfigError",
    "env",
    "is_true",
    "parse_dotenv",
    "require_uint",
]
