"""Isolated ADO review helpers (fetch-context / post-findings).

The module is invoked with ``python -m reviewforge.ado.cli`` so ADO
side effects remain in a subprocess while implementation stays in the package.
"""
from __future__ import annotations

import argparse
import hashlib
import json
import os
import sys
import urllib.parse
from pathlib import Path
from typing import Any
from types import SimpleNamespace
from ..exceptions import ReviewForgeError, emit_domain_error
from ..exceptions import AdoApiError

# Re-exports retained for older test suites and external consumers.
from .client import (  # noqa: F401  (re-exports)
    AdoClient,
    get_pr,
    normalize_ado_segment,
    parse_pr_url,
    resolve_branches,
    resolve_token,
)
from .diff_mapper import (  # noqa: F401  (re-exports)
    DiffLineMapper,
    line_set_for_file,
    map_file_line_to_diff_position,
    map_file_to_fallback,
)
from .posting import (  # noqa: F401  (re-exports)
    as_general_comment,
    dedupe_key,
    existing_bot_markers,
    find_stale_bot_threads,
    is_work_item_finding,
    should_post,
    stale_comment_body,
)
from .comment_format import (  # noqa: F401  (re-exports)
    CommentFormatter,
    DefaultCommentFormatter,
    TemplateCommentFormatter,
    build_formatter,
)
from ..artifacts.builder import (  # noqa: F401  (re-exports)
    read_json,
    write_json,
)


# --- Domain constants -------------------------------------------------------

SEV_RANK: dict[str, int] = {"nit": 1, "minor": 2, "major": 3, "blocker": 4}
SEV_LABEL: dict[str, str] = {
    "blocker": "🔴 blocker",
    "major": "🟠 major",
    "minor": "🟡 minor",
    "nit": "⚪ nit",
}
VOTE_WAITING: int = -5
MARKER: str = "prb"

# Order matches ``config._ENV_ALIASES["ado_token"]``: Azure Pipelines
# provides ``SYSTEM_ACCESSTOKEN`` first, so that wins when present.
_TOKEN_ENV_KEYS = (
    "SYSTEM_ACCESSTOKEN",
    "ADO_AUTH_TOKEN",
    "ADO_MCP_AUTH_TOKEN",
    "ADO_API_KEY",
)


# --- Compatibility helpers retained for external consumers ----------


def enc(value: str) -> str:
    """URL-encode a single value. Compatibility helper for older code."""
    import urllib.parse
    return urllib.parse.quote(value, safe="")


def token() -> str:
    """Return the ADO bearer token from env, or :func:`fail` with a clear error.

    Compatibility helper retained for external consumers.
    """
    value = ""
    for key in _TOKEN_ENV_KEYS:
        value = os.environ.get(key, "") or ""
        if value:
            break
    if not value:
        fail(
            "Missing required config: ADO_AUTH_TOKEN "
            "(aliases: SYSTEM_ACCESSTOKEN, ADO_MCP_AUTH_TOKEN, ADO_API_KEY)."
        )
    return value


def _retry_int_env(name: str, default: int) -> int:
    raw = os.getenv(name)
    if not raw:
        return default
    try:
        return int(raw)
    except ValueError:
        fail(f"{name} must be an integer, got {raw!r}")


def _retry_float_env(name: str, default: float) -> float:
    raw = os.getenv(name)
    if not raw:
        return default
    try:
        return float(raw)
    except ValueError:
        fail(f"{name} must be a number, got {raw!r}")


def _client_kwargs(args: Any) -> dict[str, Any]:
    token_value = getattr(args, "token", None)
    if not token_value:
        token_value = token()
    return {
        "token": token_value,
        "retry_attempts": getattr(args, "retry_attempts", None)
        if getattr(args, "retry_attempts", None) is not None
        else _retry_int_env("ADO_RETRY_ATTEMPTS", 3),
        "retry_base_delay": getattr(args, "retry_base_delay", None)
        if getattr(args, "retry_base_delay", None) is not None
        else _retry_float_env("ADO_RETRY_BASE_DELAY", 1.0),
        "retry_cap_delay": getattr(args, "retry_cap_delay", None)
        if getattr(args, "retry_cap_delay", None) is not None
        else _retry_float_env("ADO_RETRY_CAP_DELAY", 30.0),
        "retry_budget_secs": getattr(args, "retry_budget_secs", None)
        if getattr(args, "retry_budget_secs", None) is not None
        else _retry_float_env("ADO_RETRY_BUDGET_SECS", 90.0),
    }


def org() -> str:
    """Return ``ADO_ORG`` or fail."""
    value = os.environ.get("ADO_ORG", "")
    if not value:
        fail("Missing required config: ADO_ORG (env: ADO_ORG).")
    return value


def project() -> str:
    """Return ``ADO_PROJECT`` or fail."""
    value = os.environ.get("ADO_PROJECT", "")
    if not value:
        fail("Missing required config: ADO_PROJECT (env: ADO_PROJECT).")
    return value


def repo() -> str:
    """Return ``ADO_REPO_ID`` or fail."""
    value = os.environ.get("ADO_REPO_ID", "")
    if not value:
        fail("Missing required config: ADO_REPO_ID (env: ADO_REPO_ID).")
    return value


def normalize_org(org: str) -> tuple[str, str]:
    """Public alias of :func:`client._normalize_org` for compatibility."""
    from .client import _normalize_org
    return _normalize_org(org)


# --- Output helpers --------------------------------------------------------


def log(message: str) -> None:
    print(f"[ado] {message}", file=sys.stderr)


def fail(message: str, code: int = 1) -> None:
    raise AdoApiError(f"[ado][ERROR] {message}")


def is_true(value: str | None) -> bool:
    return (value or "").lower() in {"1", "true", "yes", "on"}


def truncate(text: Any, max_chars: int) -> str:
    s = str(text or "")
    if len(s) <= max_chars:
        return s
    return s[: max(0, max_chars - 80)] + f"\n\n[truncated: original length {len(s)} chars]"


def fence(text: str) -> str:
    longest = 0
    current = 0
    for ch in text:
        if ch == "`":
            current += 1
            longest = max(longest, current)
        else:
            current = 0
    ticks = "`" * max(3, longest + 1)
    return f"{ticks}\n{text}\n{ticks}"


# --- ADO-specific helpers ---------------------------------------------------


def simplify_thread(thread: dict[str, Any]) -> dict[str, Any]:
    comments = thread.get("comments") or []
    first = comments[0] if comments else {}
    ctx = thread.get("threadContext") or {}
    return {
        "id": thread.get("id"),
        "status": thread.get("status"),
        "filePath": ctx.get("filePath"),
        "line": ((ctx.get("rightFileStart") or {}).get("line")),
        "firstComment": first.get("content", ""),
        "author": (first.get("author") or {}).get("displayName", "unknown"),
    }


def review_thread(thread: dict[str, Any]) -> dict[str, Any]:
    """Normalize all comment data needed for deterministic review history."""
    ctx = thread.get("threadContext") or {}
    comments = []
    for comment in thread.get("comments") or []:
        author = comment.get("author") or {}
        comments.append(
            {
                "id": comment.get("id"),
                "authorId": author.get("id"),
                "author": author.get("displayName") or "unknown",
                "content": comment.get("content") or "",
                "publishedDate": comment.get("publishedDate") or "",
            }
        )
    return {
        "id": thread.get("id"),
        "status": thread.get("status") or "",
        "threadContext": {
            "filePath": ctx.get("filePath"),
            "rightFileStart": ctx.get("rightFileStart"),
        },
        "comments": comments,
        "commitId": None,
    }


def _normalize_work_item(item: dict[str, Any]) -> dict[str, Any]:
    fields = item.get("fields") or {}
    return {
        "id": item.get("id"),
        "type": fields.get("System.WorkItemType") or "Unknown",
        "title": fields.get("System.Title") or "(untitled)",
        "state": fields.get("System.State") or "",
        "description": fields.get("System.Description") or "(none)",
        "acceptanceCriteria": fields.get("Microsoft.VSTS.Common.AcceptanceCriteria") or "(none)",
    }


def _normalize_work_item_comments(raw: dict[str, Any], work_item_id: str) -> dict[str, Any] | None:
    comments = [
        {
            "id": comment.get("id"),
            "author": ((comment.get("author") or {}).get("displayName")) or "unknown",
            "text": comment.get("text") or "",
        }
        for comment in raw.get("comments", [])
    ]
    return {"workItemId": work_item_id, "comments": comments} if comments else None


def fetch_work_items(
    client: AdoClient, pr: dict[str, Any]
) -> tuple[list[dict[str, Any]], list[dict[str, Any]]]:
    """Fetch linked work items + comments. Returns (work_items, comments_by_item)."""
    refs = pr.get("workItemRefs") or []
    ids = [str(ref.get("id")) for ref in refs if ref.get("id") is not None]
    if not ids:
        return [], []
    body = {
        "ids": ids,
        "fields": [
            "System.Title",
            "System.Description",
            "Microsoft.VSTS.Common.AcceptanceCriteria",
            "System.WorkItemType",
            "System.State",
        ],
    }
    batch = client.post("/_apis/wit/workItemsBatch?api-version=7.1-preview.1", body)
    work_items = [_normalize_work_item(item) for item in batch.get("value", [])]
    comments_by_item = []
    for work_item_id in ids:
        raw = client.get(
            f"/_apis/wit/workItems/{urllib.parse.quote(work_item_id)}/comments?api-version=7.1-preview.4"
        )
        if comments := _normalize_work_item_comments(raw, work_item_id):
            comments_by_item.append(comments)
    return work_items, comments_by_item


def current_reviewer_id(client: AdoClient, pr: dict[str, Any]) -> str | None:
    """Return the authenticated user's reviewer id on the PR, or None."""
    me = client.connection_data().get("authenticatedUser") or {}
    me_id = me.get("id")
    me_name = (me.get("uniqueName") or "").lower()
    for r in pr.get("reviewers") or []:
        if me_id and r.get("id") == me_id:
            return r.get("id")
        if me_name and (r.get("uniqueName") or "").lower() == me_name:
            return r.get("id")
    return None


def comment_body(f: dict[str, Any], key: str, max_chars: int, summary: str | None = None) -> str:
    """Back-compat shim. Delegates to :class:`DefaultCommentFormatter`.

    New code should call :func:`build_formatter` directly so that
    ``COMMENT_TEMPLATE_PATH`` (when set) is honoured.
    """
    return DefaultCommentFormatter().format(f, key=key, max_chars=max_chars, summary=summary)


# --- JSON helpers ---------------------------------------------------------


def extract_json(path: Path) -> dict[str, Any]:
    """Read a JSON file, stripping Markdown code fences if present."""
    text = path.read_text(encoding="utf-8").strip()
    try:
        return json.loads(text)
    except json.JSONDecodeError:
        # Strip code fences line-by-line.
        stripped = "\n".join(
            line for line in text.splitlines() if not line.strip().startswith("```")
        )
        return json.loads(stripped)


def _normalize_finding_evidence(evidence: dict[str, Any]) -> dict[str, Any]:
    return {
        "changedLines": [
            value for value in (evidence.get("changed_lines") or []) if isinstance(value, int)
        ],
        "contextFilesRead": [
            value for value in (evidence.get("context_files_read") or []) if isinstance(value, str)
        ],
        "whyNewInThisPr": str(evidence.get("why_new_in_this_pr") or "").strip(),
        "whyNotIntentional": str(evidence.get("why_not_intentional") or "").strip(),
    }


def _normalized_finding(
    finding: dict[str, Any],
    severity: str,
    confidence: str | None,
) -> dict[str, Any]:
    normalized = {
        "severity": severity,
        "title": finding["title"].strip(),
        "message": finding["message"],
        "file": finding.get("file"),
        "line": finding.get("line"),
        "confidence": confidence,
        "contextBasis": finding.get("contextBasis"),
        "suggestion": finding.get("suggestion"),
        "anchorDowngraded": bool(finding.get("anchorDowngraded")),
    }
    if isinstance(normalized["file"], str) and normalized["file"].startswith("/"):
        normalized["file"] = normalized["file"].lstrip("/")
    if evidence := finding.get("evidence") or {}:
        normalized["evidence"] = _normalize_finding_evidence(evidence)
    return normalized


def _validate_finding(finding: Any) -> dict[str, Any]:
    if not isinstance(finding, dict):
        fail("finding is not an object")
    severity = finding.get("severity")
    if severity not in SEV_RANK:
        fail(f"invalid severity {severity!r}; expected one of {list(SEV_RANK)}")
    if not isinstance(finding.get("title"), str) or not finding["title"].strip():
        fail("finding missing non-empty title")
    if not isinstance(finding.get("message"), str) or not finding["message"].strip():
        fail("finding missing non-empty message")
    confidence = finding.get("confidence")
    if confidence is not None and confidence not in ("high", "medium", "low"):
        fail(f"invalid confidence {confidence!r}")
    return _normalized_finding(finding, severity, confidence)


def validate_findings(
    doc: dict[str, Any],
) -> tuple[str, list[dict[str, Any]]]:
    """Validate the review doc shape and normalize findings."""
    if not isinstance(doc, dict):
        fail("review doc is not an object")
    if not isinstance(doc.get("summary"), str):
        fail("review doc summary must be a string")
    summary = str(doc.get("summary") or "").strip()
    findings_raw = doc.get("findings") or []
    if not isinstance(findings_raw, list):
        fail("findings must be a list")
    return summary, [_validate_finding(finding) for finding in findings_raw]


def worst_rank(findings: list[dict[str, Any]]) -> int:
    """Return the highest severity rank present in ``findings`` (0 for empty)."""
    if not findings:
        return 0
    return max(SEV_RANK.get(f.get("severity"), 0) for f in findings)


def should_threshold(findings: list[dict[str, Any]], threshold: str) -> bool:
    """Return ``True`` iff ``findings`` has at least one severity at/above threshold."""
    if threshold in (None, "none", ""):
        return False
    if threshold not in SEV_RANK:
        return False
    return worst_rank(findings) >= SEV_RANK[threshold]


# Backward-compatible alias: older consumers exposed
# ``key_of`` rather than ``dedupe_key``. Keep the alias so existing
# test imports keep working.
key_of = dedupe_key


def _authenticated_user(client: AdoClient) -> dict[str, Any]:
    try:
        payload = client.connection_data()
    except Exception:
        return {}
    return (payload.get("authenticatedUser") if isinstance(payload, dict) else {}) or {}


def _commit_context(client: AdoClient, pr_id: int | str) -> list[dict[str, Any]]:
    try:
        payload = client.get_commits(pr_id)
    except Exception:
        return []
    return [
        {
            "commitId": commit.get("commitId") or commit.get("id"),
            "authorId": (commit.get("author") or {}).get("id"),
            "authorDate": (commit.get("author") or {}).get("date"),
            "committerDate": (commit.get("committer") or {}).get("date"),
        }
        for commit in payload
        if isinstance(commit, dict)
    ] if isinstance(payload, list) else []


def _reviewer_context(authenticated: dict[str, Any]) -> dict[str, Any] | None:
    if not authenticated.get("id"):
        return None
    return {
        "id": authenticated.get("id"),
        "displayName": authenticated.get("displayName") or "",
        "uniqueName": authenticated.get("uniqueName") or "",
        "descriptor": authenticated.get("descriptor") or "",
    }


def _context_metadata(
    client: AdoClient,
    args: argparse.Namespace,
    pr: dict[str, Any],
    raw_threads: list[dict[str, Any]],
    authenticated: dict[str, Any],
    commits: list[dict[str, Any]],
) -> dict[str, Any]:
    source_commit = (
        (pr.get("lastMergeSourceCommit") or {}).get("commitId")
        or (pr.get("lastMergeCommit") or {}).get("commitId")
    )
    return {
        "org": client.org_name,
        "project": args.project,
        "repositoryId": args.repo,
        "pullRequestId": args.pr,
        "title": pr.get("title") or "",
        "description": pr.get("description") or "",
        "status": pr.get("status") or "",
        "isDraft": bool(pr.get("isDraft")),
        "sourceRefName": pr.get("sourceRefName") or "",
        "targetRefName": pr.get("targetRefName") or "",
        "sourceCommit": source_commit or "",
        "createdBy": pr.get("createdBy") or None,
        "reviewers": pr.get("reviewers") or [],
        "reviewState": {
            "reviewer": _reviewer_context(authenticated),
            "threads": [review_thread(thread) for thread in raw_threads],
            "commits": commits,
            "currentCommit": source_commit,
        },
    }


def command_fetch_context(args: argparse.Namespace) -> int:
    """Fetch normalized PR metadata and review history."""
    client = AdoClient(args.org, args.project, args.repo, **_client_kwargs(args))
    out = Path(args.out)
    log(f"fetching PR #{args.pr} context")
    pr = client.get_pr(args.pr, include_work_item_refs=True)
    work_items, work_item_comments = fetch_work_items(client, pr)
    raw_threads = client.get_threads(args.pr)
    metadata = _context_metadata(
        client,
        args,
        pr,
        raw_threads,
        _authenticated_user(client),
        _commit_context(client, args.pr),
    )
    context = {
        "pr": metadata,
        "workItems": work_items,
        "workItemComments": work_item_comments,
        "existingThreads": [simplify_thread(thread) for thread in raw_threads],
    }
    for name, value in {
        "metadata.json": metadata,
        "work-items.json": work_items,
        "work-item-comments.json": work_item_comments,
        "threads.json": context["existingThreads"],
        "context.json": context,
    }.items():
        write_json(out / name, value)
    log(f"wrote ADO context to {out}")
    return 0


def _severity_rank(value: str | None) -> int | None:
    severity = (value or "none").strip().lower()
    if severity == "none":
        return None
    if severity in SEV_RANK:
        return SEV_RANK[severity]
    fail(f"POST_MIN_SEVERITY must be one of: {list(SEV_RANK)}")


def _required_context_severities(raw: str) -> set[str]:
    required = {item.strip() for item in raw.split(",") if item.strip()}
    invalid = required - SEV_RANK.keys()
    if invalid:
        fail(f"REQUIRE_CONTEXT_FOR contains invalid severity(s): {invalid}")
    return required


def _has_required_context(finding: dict[str, Any]) -> bool:
    evidence = finding.get("evidence") or {}
    return bool(evidence.get("contextFilesRead")) or finding.get("contextBasis") in {
        "surrounding-code-read",
        "full-module-review",
    }


def _keep_context_finding(
    finding: dict[str, Any],
    required: set[str],
    raw: str,
) -> bool:
    if finding["severity"] not in required or _has_required_context(finding):
        return True
    log(
        f"dropped finding '{finding['title']}' ({finding['severity']}): "
        f"REQUIRE_CONTEXT_FOR={raw} but no context files read"
    )
    return False


def _max_findings_value(raw: str | None) -> int | None:
    if not raw:
        return None
    try:
        value = int(raw)
    except ValueError:
        fail(f"MAX_FINDINGS must be an integer, got {raw!r}")
    if value < 0:
        fail("MAX_FINDINGS must be non-negative")
    return value


def _passes_finding_filters(
    finding: dict[str, Any],
    post_min_rank: int | None,
    drop_low: bool,
) -> bool:
    return (
        (post_min_rank is None or SEV_RANK[finding["severity"]] >= post_min_rank)
        and not (drop_low and finding.get("confidence") == "low")
    )


def _filter_context_findings(
    findings: list[dict[str, Any]],
    required: set[str],
    raw: str,
) -> list[dict[str, Any]]:
    return [
        finding
        for finding in findings
        if _keep_context_finding(finding, required, raw)
    ]


def _filter_findings(
    findings: list[dict[str, Any]],
    *,
    post_min_severity: str | None = None,
    drop_low_confidence: bool | None = None,
    require_context_for: str | None = None,
    max_findings: str | None = None,
) -> list[dict[str, Any]]:
    """Apply POST_MIN_SEVERITY / DROP_LOW_CONFIDENCE / REQUIRE_CONTEXT_FOR / MAX_FINDINGS."""
    post_min = post_min_severity or os.getenv("POST_MIN_SEVERITY", "none") or "none"
    post_min_rank = _severity_rank(post_min)
    drop_low = (
        bool(drop_low_confidence)
        if drop_low_confidence is not None
        else is_true(os.getenv("DROP_LOW_CONFIDENCE"))
    )
    context_raw = require_context_for if require_context_for is not None else os.getenv("REQUIRE_CONTEXT_FOR", "")
    required = _required_context_severities(context_raw)
    filtered = [
        finding
        for finding in findings
        if _passes_finding_filters(finding, post_min_rank, drop_low)
    ]
    if required:
        filtered = _filter_context_findings(filtered, required, context_raw)
    limit = _max_findings_value(
        max_findings if max_findings is not None else os.getenv("MAX_FINDINGS")
    )
    if limit is not None and len(filtered) > limit:
        filtered = sorted(
            filtered, key=lambda finding: SEV_RANK[finding["severity"]], reverse=True
        )[:limit]
        log(f"capped findings MAX_FINDINGS={limit}")
    return filtered


def _legacy_file_line_context(finding: dict[str, Any]) -> dict[str, Any] | None:
    if not (finding.get("file") and finding.get("line")):
        return None
    line = finding["line"]
    return {
        "filePath": "/" + str(finding["file"]).lstrip("/"),
        "rightFileStart": {"line": line, "offset": 1},
        "rightFileEnd": {"line": line, "offset": 1},
    }


def _finding_thread_context(
    finding: dict[str, Any],
    mapper: DiffLineMapper | None,
) -> tuple[dict[str, Any] | None, bool, bool]:
    if not finding.get("file"):
        return None, False, False
    context = map_file_line_to_diff_position(
        finding.get("file"), finding.get("line"), mapper=mapper
    )
    if context is not None:
        return context.to_thread_context(), False, False
    fallback = map_file_to_fallback(finding["file"], mapper=mapper)
    if fallback is not None:
        return fallback.to_thread_context(), True, False
    if mapper is None:
        return _legacy_file_line_context(finding), False, False
    return None, False, True


def _skip_finding(result: dict[str, Any], finding: dict[str, Any], key: str, reason: str) -> None:
    result["skipped"] += 1
    result["skipped_reasons"][reason] += 1
    log(f"skipped finding '{finding['title']}' (key={key}): {reason.replace('_', ' ')}")


def _post_one_finding(
    client: AdoClient,
    pr_id: int | str,
    finding: dict[str, Any],
    existing: set[str],
    mapper: DiffLineMapper | None,
    summary: str,
    result: dict[str, Any],
) -> None:
    if is_work_item_finding(finding):
        finding = as_general_comment(finding)
    key = key_of(finding)
    if finding.get("anchorDowngraded"):
        _skip_finding(result, finding, key, "no_line_mapping")
        return
    if not should_post(finding, existing):
        result["skipped"] += 1
        result["skipped_reasons"]["duplicate"] += 1
        log(f"skipping duplicate finding '{finding['title']}' (key={key})")
        return
    thread_body: dict[str, Any] = {
        "comments": [
            {
                "content": build_formatter().format(
                    finding, key=key, max_chars=20000, summary=summary
                ),
                "commentType": "text",
            }
        ],
        "status": "active",
    }
    context, fallback, unmappable = _finding_thread_context(finding, mapper)
    if unmappable:
        _skip_finding(result, finding, key, "no_line_mapping")
        return
    if context is not None:
        thread_body["threadContext"] = context
    if fallback:
        result["skipped_reasons"]["file_fallback"] += 1
    response = client.create_thread(pr_id, thread_body)
    result["created"] += 1
    result["comments"].append(
        {
            "key": key,
            "threadId": (response or {}).get("id"),
            "title": finding["title"],
            "severity": finding["severity"],
        }
    )


def _annotate_stale(
    client: AdoClient,
    pr_id: int | str,
    pr: dict[str, Any],
    threads: list[dict[str, Any]],
    existing: set[str],
    mapper: DiffLineMapper | None,
    result: dict[str, Any],
) -> None:
    if os.getenv("ANNOTATE_STALE", "1") == "0" or mapper is None:
        return
    just_posted = {comment["threadId"] for comment in result["comments"] if comment.get("threadId")}
    stale = find_stale_bot_threads(
        threads,
        existing,
        _build_diff_anchors(mapper),
        just_posted_thread_ids=just_posted,
    )
    if not stale:
        return
    short_sha = _short_sha(pr) or ""
    annotated: list[int | str] = []
    for entry in stale:
        try:
            client.add_comment(
                pr_id,
                entry["threadId"],
                stale_comment_body(short_sha=short_sha, key=entry.get("key")),
            )
            annotated.append(entry["threadId"])
        except Exception as exc:  # noqa: BLE001 — best-effort
            log(f"failed to annotate stale thread {entry['threadId']}: {exc}")
    result["annotated_stale"] = len(annotated)
    result["stale_thread_ids"] = annotated
    log(f"annotated {len(annotated)} stale thread(s)")


def _apply_vote(
    client: AdoClient,
    pr_id: int | str,
    pr: dict[str, Any],
    findings: list[dict[str, Any]],
    args: argparse.Namespace,
    result: dict[str, Any],
) -> None:
    value = (getattr(args, "vote_waiting_on", None) or os.getenv("VOTE_WAITING_ON", "none")).strip().lower()
    if value == "none":
        return
    if value not in SEV_RANK:
        fail(f"VOTE_WAITING_ON must be one of: {list(SEV_RANK)}")
    if not any(SEV_RANK[finding["severity"]] >= SEV_RANK[value] for finding in findings):
        return
    reviewer_id = current_reviewer_id(client, pr)
    if reviewer_id:
        client.vote(pr_id, reviewer_id, VOTE_WAITING)
        result["vote"] = {"reviewer_id": reviewer_id, "value": VOTE_WAITING}
        result["votedWaitingForAuthor"] = True


def _apply_fail_on(
    findings: list[dict[str, Any]],
    args: argparse.Namespace,
    result: dict[str, Any],
) -> bool:
    value = (getattr(args, "fail_on", None) or os.getenv("FAIL_ON", "none")).strip().lower()
    if value == "none" or not any(
        SEV_RANK[finding["severity"]] >= SEV_RANK.get(value, 99) for finding in findings
    ):
        return False
    log(f"FAIL_ON={value} threshold met; exiting 1")
    result["failOnTriggered"] = True
    return True


def command_post_findings(args: argparse.Namespace) -> int:
    """Post findings to ADO. Idempotent: skips findings already present."""
    client = AdoClient(args.org, args.project, args.repo, **_client_kwargs(args))
    summary, findings = validate_findings(extract_json(Path(args.findings)))
    parsed_count = len(findings)
    findings = _filter_findings(
        findings,
        post_min_severity=getattr(args, "post_min_severity", None),
        drop_low_confidence=getattr(args, "drop_low_confidence", None),
        require_context_for=getattr(args, "require_context_for", None),
        max_findings=getattr(args, "max_findings", None),
    )
    pr = client.get_pr(args.pr)
    threads = client.get_threads(args.pr)
    existing = existing_bot_markers(threads)
    diff_path = Path(args.out).parent / "diff.patch"
    diff_text = diff_path.read_text(encoding="utf-8") if diff_path.exists() else ""
    mapper = DiffLineMapper.from_text(diff_text) if diff_text else None
    out = Path(args.out)
    out.parent.mkdir(parents=True, exist_ok=True)
    result: dict[str, Any] = {
        "summary": summary,
        "parsed": parsed_count,
        "accepted": len(findings),
        "created": 0,
        "skipped": 0,
        "skipped_reasons": {"duplicate": 0, "no_line_mapping": 0, "file_fallback": 0},
        "comments": [],
        "votedWaitingForAuthor": False,
        "failOnTriggered": False,
    }
    for finding in findings:
        _post_one_finding(client, args.pr, finding, existing, mapper, summary, result)
    _annotate_stale(client, args.pr, pr, threads, existing, mapper, result)
    _apply_vote(client, args.pr, pr, findings, args, result)
    if _apply_fail_on(findings, args, result):
        write_json(out, result)
        return 1
    write_json(out, result)
    log(
        f"parsed {parsed_count} finding(s); {len(findings)} accepted for posting; "
        f"skipped {result['skipped']} already-present finding(s)"
    )
    return 0


def fetch_pr_context(cfg: Any, out_dir: Path) -> dict[str, Any]:  # pragma: no cover
    """Fetch normalized PR context without crossing a process boundary."""
    args = SimpleNamespace(
        org=cfg.ado_org,
        project=cfg.ado_project,
        repo=cfg.ado_repo_id,
        pr=int(cfg.pr_id),
        out=str(out_dir),
        token=cfg.ado_token,
        retry_attempts=cfg.ado_retry_attempts,
        retry_base_delay=cfg.ado_retry_base_delay,
        retry_cap_delay=cfg.ado_retry_cap_delay,
        retry_budget_secs=cfg.ado_retry_budget_secs,
    )
    command_fetch_context(args)
    return read_json(out_dir / "context.json") or {}


def _posting_arg(cfg: Any, name: str) -> Any:
    """Return the cfg posting value, or ``None`` when it is the field default.

    ``None`` lets ``command_post_findings`` fall back to the environment, so
    a directly-constructed ``Config`` (e.g. in tests or embedders) does not
    shadow ``POST_MIN_SEVERITY`` and friends with defaults.
    """
    import dataclasses
    value = getattr(cfg, name, None)
    try:
        default = next(
            f.default for f in dataclasses.fields(type(cfg)) if f.name == name
        )
    except (TypeError, StopIteration):
        return value
    return None if value == default else value


def post_findings(cfg: Any, findings_path: Path, out_path: Path) -> dict[str, Any]:  # pragma: no cover
    args = SimpleNamespace(
        org=cfg.ado_org,
        project=cfg.ado_project,
        repo=cfg.ado_repo_id,
        pr=int(cfg.pr_id),
        findings=str(findings_path),
        out=str(out_path),
        token=cfg.ado_token,
        retry_attempts=cfg.ado_retry_attempts,
        retry_base_delay=cfg.ado_retry_base_delay,
        retry_cap_delay=cfg.ado_retry_cap_delay,
        retry_budget_secs=cfg.ado_retry_budget_secs,
        post_min_severity=_posting_arg(cfg, "post_min_severity"),
        drop_low_confidence=_posting_arg(cfg, "drop_low_confidence"),
        require_context_for=_posting_arg(cfg, "require_context_for"),
        max_findings=str(_posting_arg(cfg, "max_findings") or ""),
        vote_waiting_on=_posting_arg(cfg, "vote_waiting_on"),
        fail_on=_posting_arg(cfg, "fail_on"),
    )
    code = command_post_findings(args)
    result = read_json(out_path) or {}
    if code and result.get("failOnTriggered"):
        raise AdoApiError(
            "[review][ERROR] post-findings failed",
            details={"exit_code": code, "result": result},
        )
    if code:
        raise AdoApiError(
            "[review][ERROR] post-findings failed",
            details={"exit_code": code},
        )
    return result


# --- CLI ------------------------------------------------------------------


def build_parser() -> argparse.ArgumentParser:
    """Build the legacy argparse parser for ``fetch-context`` / ``post-findings``."""
    parser = argparse.ArgumentParser(description="ReviewForge Azure DevOps helper")
    sub = parser.add_subparsers(dest="command", required=True)

    common = argparse.ArgumentParser(add_help=False)
    common.add_argument("--org", required=True)
    common.add_argument("--project", required=True)
    common.add_argument("--repo", required=True)
    common.add_argument("--pr", required=True, type=int)

    fetch = sub.add_parser("fetch-context", parents=[common])
    fetch.add_argument("--out", required=True)
    fetch.set_defaults(func=command_fetch_context)

    post = sub.add_parser("post-findings", parents=[common])
    post.add_argument("--findings", required=True)
    post.add_argument("--out", required=True)
    post.set_defaults(func=command_post_findings)

    return parser


def main(argv: list[str] | None = None) -> int:
    args = build_parser().parse_args(argv)
    try:
        return int(args.func(args))
    except ReviewForgeError as exc:
        emit_domain_error(exc)
        return 1


def _build_diff_anchors(mapper: DiffLineMapper | None) -> dict[str, set[int]]:
    """Build ``{file_path: set_of_new_file_lines}`` from a diff mapper.

    A missing mapper produces an empty mapping, but callers skip reconciliation
    before invoking this helper. Missing diff data therefore fails closed:
    existing bot anchors are not treated as stale without a current diff.
    """
    if mapper is None:
        return {}
    out: dict[str, set[int]] = {}
    for f in mapper._files:  # noqa: SLF001 — accessing the file index
        anchors = mapper.line_set(f.path)
        if anchors:
            out[f.path.lstrip("/")] = anchors
    return out


def _short_sha(pr: dict[str, Any]) -> str | None:
    """Return the PR's source commit short SHA, if available."""
    last = pr.get("lastMergeCommit") or pr.get("lastMergeSourceCommit")
    if isinstance(last, dict):
        cid = last.get("commitId")
        if isinstance(cid, str) and cid:
            return cid[:8]
    return None


__all__ = [
    "MARKER",
    "SEV_LABEL",
    "SEV_RANK",
    "VOTE_WAITING",
    "build_parser",
    "command_fetch_context",
    "command_post_findings",
    "comment_body",
    "current_reviewer_id",
    "dedupe_key",
    "enc",
    "extract_json",
    "fail",
    "fetch_work_items",
    "fence",
    "is_true",
    "key_of",
    "log",
    "main",
    "org",
    "project",
    "repo",
    "should_threshold",
    "simplify_thread",
    "token",
    "truncate",
    "validate_findings",
    "worst_rank",
]
if __name__ == "__main__":  # pragma: no cover
    raise SystemExit(main())
