from __future__ import annotations

from dataclasses import dataclass
from pathlib import Path
import os, re, sys


def is_true(value: str | None) -> bool:
    return (value or "").lower() in {"1", "true", "yes", "on"}


def require_uint(name: str, value: str) -> int:
    if not re.fullmatch(r"\d+", value):
        raise SystemExit(f"[review][ERROR] {name} must be a non-negative integer, got: {value}")
    return int(value)


def env(name: str, default: str | None = None) -> str:
    value = os.getenv(name)
    if value is None or value == "":
        if default is None:
            raise SystemExit(f"[review][ERROR] {name} is required")
        return default
    return value


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

    @classmethod
    def from_env(cls) -> "Config":
        token = os.getenv("ADO_AUTH_TOKEN") or os.getenv("ADO_MCP_AUTH_TOKEN")
        if not token:
            raise SystemExit("[review][ERROR] ADO_AUTH_TOKEN or ADO_MCP_AUTH_TOKEN is required")
        os.environ["ADO_AUTH_TOKEN"] = token
        os.environ.setdefault("ADO_MCP_AUTH_TOKEN", token)

        pr_url = os.getenv("PR_URL")
        if pr_url:
            from infrastructure.ado.client import parse_pr_url
            org, project, repo, pr_id = parse_pr_url(pr_url)
            os.environ.update({"ADO_ORG": org, "ADO_PROJECT": project, "ADO_REPO_ID": repo, "PR_ID": pr_id})

        workspace = Path(env("WORKSPACE", "/workspace"))
        max_diff = require_uint("MAX_DIFF_BYTES", env("MAX_DIFF_BYTES", "200000"))
        return cls(
            ado_org=env("ADO_ORG"),
            ado_project=env("ADO_PROJECT"),
            ado_repo_id=env("ADO_REPO_ID"),
            pr_id=env("PR_ID"),
            ado_token=token,
            source_branch=os.getenv("SOURCE_BRANCH") or os.getenv("SYSTEM_PULLREQUEST_SOURCEBRANCH") or "",
            target_branch=os.getenv("TARGET_BRANCH") or os.getenv("SYSTEM_PULLREQUEST_TARGETBRANCH") or "",
            workspace=workspace,
            clone_root=Path(env("CLONE_ROOT", str(workspace))),
            review_language=env("REVIEW_LANGUAGE", "English"),
            review_prompt_path=Path(env("REVIEW_PROMPT_PATH", "/app/prompts/review-system.md")),
            intent_prompt_path=Path(env("REVIEW_INTENT_PROMPT_PATH", "/app/prompts/intent.md")),
            context_plan_prompt_path=Path(env("REVIEW_CONTEXT_PLAN_PROMPT_PATH", "/app/prompts/context-plan.md")),
            context_digest_prompt_path=Path(env("REVIEW_CONTEXT_DIGEST_PROMPT_PATH", "/app/prompts/context-digest.md")),
            verify_prompt_path=Path(env("REVIEW_VERIFY_PROMPT_PATH", "/app/prompts/verify-findings.md")),
            severity_prompt_path=Path(env("REVIEW_SEVERITY_PROMPT_PATH", "/app/prompts/severity.md")),
            standards_path=Path(env("REVIEW_STANDARDS_PATH", "/app/standards/clean-code.md")),
            pi_model=env("PI_MODEL", "openai/gpt-5.5"),
            max_diff_bytes=max_diff,
            chunk_trigger_diff_bytes=require_uint("CHUNK_TRIGGER_DIFF_BYTES", env("CHUNK_TRIGGER_DIFF_BYTES", str(max_diff))),
            disable_chunk_review=is_true(os.getenv("DISABLE_CHUNK_REVIEW")),
            pi_timeout_secs=require_uint("PI_TIMEOUT_SECS", env("PI_TIMEOUT_SECS", "600")),
            dry_run=is_true(env("DRY_RUN", "0")),
            include_work_items=is_true(env("INCLUDE_WORK_ITEMS", "1")),
            include_existing_comments=is_true(env("INCLUDE_EXISTING_COMMENTS", "1")),
            verify_findings=env("VERIFY_FINDINGS", "1") != "0",
            force_review=is_true(env("FORCE_REVIEW", "0")),
            review_target_branches=os.getenv("REVIEW_TARGET_BRANCHES", ""),
            review_artifact_dir=os.getenv("REVIEW_ARTIFACT_DIR"),
            review_artifact_root=Path(env("REVIEW_ARTIFACT_ROOT", str(workspace / "artifacts"))),
            review_run_id=os.getenv("REVIEW_RUN_ID"),
        )

    def validate_files(self) -> None:
        for path in [self.review_prompt_path, self.intent_prompt_path, self.context_plan_prompt_path, self.context_digest_prompt_path, self.verify_prompt_path, self.severity_prompt_path, self.standards_path]:
            if not path.is_file():
                raise SystemExit(f"[review][ERROR] required prompt/standards file not readable: {path}")
