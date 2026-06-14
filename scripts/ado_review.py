#!/usr/bin/env python3
"""Azure DevOps PR review integration helpers.

Thin wrapper around :mod:`auto_pr_reviewer` for backward compatibility with
existing Dockerfiles and PowerShell wrappers. The two subcommands
(``fetch-context`` and ``post-findings``) match the original CLI shape; all
business logic lives in the package.
"""
from __future__ import annotations

import argparse
import hashlib
import json
import os
import sys
import textwrap
import urllib.error
import urllib.parse
import urllib.request
from pathlib import Path
from typing import Any


# --- Re-export helpers used by older code ----------------------------------


SEV_RANK = {"nit": 1, "minor": 2, "major": 3, "blocker": 4}
SEV_LABEL = {
    "blocker": "🔴 blocker",
    "major": "🟠 major",
    "minor": "🟡 minor",
    "nit": "⚪ nit",
}
VOTE_WAITING = -5
MARKER = "prb"


def log(message: str) -> None:
    print(f"[ado] {message}", file=sys.stderr)


def fail(message: str, code: int = 1) -> None:
    print(f"[ado][ERROR] {message}", file=sys.stderr)
    raise SystemExit(code)


def normalize_org(org: str) -> tuple[str, str]:
    """Return ``(org_url, short_name)`` from a raw org string or URL.

    Public alias of the package's private helper, used by the test suite.
    """
    return _normalize_org_public(org)


def _normalize_org_public(org: str) -> tuple[str, str]:
    raw = (org or "").strip().rstrip("/")
    if raw.startswith("https://"):
        if "dev.azure.com/" in raw:
            short = raw.split("dev.azure.com/", 1)[1].split("/", 1)[0]
            return raw, short
        host = __import__("urllib.parse").parse.urlparse(raw).hostname or ""
        if host.endswith(".visualstudio.com"):
            return raw, host.split(".", 1)[0]
        raise SystemExit(f"[ado][ERROR] Could not derive organization name from URL: {org}")
    if "/" in raw or "." in raw:
        raise SystemExit(f"[ado][ERROR] Could not derive organization name from URL: {org}")
    return f"https://dev.azure.com/{raw}", raw


def enc(value: str) -> str:
    """URL-encode a single value."""
    return urllib.parse.quote(value, safe="")


def token() -> str:
    """Read the ADO bearer token from env or fail with a clear error."""
    return resolve_token()


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


def fetch_work_items(client: AdoClient, pr: dict[str, Any]) -> tuple[list[dict[str, Any]], list[dict[str, Any]]]:
    """Public alias of :func:`_fetch_work_items`."""
    return _fetch_work_items(client, pr)


# --- Delegate to the package ----------------------------------------------


def _ensure_src_on_path() -> None:
    here = os.path.dirname(os.path.abspath(__file__))
    src = os.path.normpath(os.path.join(here, "..", "src"))
    if os.path.isdir(src) and src not in sys.path:
        sys.path.insert(0, src)


_ensure_src_on_path()
from auto_pr_reviewer.ado.client import (  # noqa: E402  (after sys.path tweak)
    AdoClient,
    resolve_branches,
    resolve_token,
)
from auto_pr_reviewer.ado.posting import (  # noqa: E402
    dedupe_key as key_of,
    existing_bot_markers,
    should_post,
)
from auto_pr_reviewer.artifacts.builder import (  # noqa: E402
    read_json as _read_json,
    write_json,
)


# --- Subcommands ----------------------------------------------------------


def command_fetch_context(args: argparse.Namespace) -> int:
    client = AdoClient(args.org, args.project, args.repo)
    out = Path(args.out)
    log(f"fetching PR #{args.pr} context")
    pr = client.get_pr(args.pr, include_work_item_refs=True)
    work_items, work_item_comments = _fetch_work_items(client, pr)
    threads = [simplify_thread(t) for t in client.get_threads(args.pr)]
    metadata = {
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
        "createdBy": pr.get("createdBy") or None,
        "reviewers": pr.get("reviewers") or [],
    }
    context = {
        "pr": metadata,
        "workItems": work_items,
        "workItemComments": work_item_comments,
        "existingThreads": threads,
    }
    write_json(out / "metadata.json", metadata)
    write_json(out / "work-items.json", work_items)
    write_json(out / "work-item-comments.json", work_item_comments)
    write_json(out / "threads.json", threads)
    write_json(out / "context.json", context)
    log(f"wrote ADO context to {out}")
    return 0


def _fetch_work_items(client: AdoClient, pr: dict[str, Any]) -> tuple[list[dict[str, Any]], list[dict[str, Any]]]:
    refs = pr.get("workItemRefs") or []
    ids = [str(r.get("id")) for r in refs if r.get("id") is not None]
    if not ids:
        return [], []
    body = {"ids": ids, "fields": [
        "System.Title",
        "System.Description",
        "Microsoft.VSTS.Common.AcceptanceCriteria",
        "System.WorkItemType",
        "System.State",
    ]}
    batch = client.post("/_apis/wit/workItemsBatch?api-version=7.1-preview.1", body)
    work_items: list[dict[str, Any]] = []
    for item in batch.get("value", []):
        fields = item.get("fields") or {}
        work_items.append({
            "id": item.get("id"),
            "type": fields.get("System.WorkItemType") or "Unknown",
            "title": fields.get("System.Title") or "(untitled)",
            "state": fields.get("System.State") or "",
            "description": fields.get("System.Description") or "(none)",
            "acceptanceCriteria": fields.get("Microsoft.VSTS.Common.AcceptanceCriteria") or "(none)",
        })
    comments_by_item: list[dict[str, Any]] = []
    for wid in ids:
        raw = client.get(f"/_apis/wit/workItems/{urllib.parse.quote(wid)}/comments?api-version=7.1-preview.4")
        comments = [
            {
                "id": c.get("id"),
                "author": ((c.get("author") or {}).get("displayName")) or "unknown",
                "text": c.get("text") or "",
            }
            for c in raw.get("comments", [])
        ]
        if comments:
            comments_by_item.append({"workItemId": wid, "comments": comments})
    return work_items, comments_by_item


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


def extract_json(path: Path) -> dict[str, Any]:
    """Read JSON, tolerating Markdown code fences."""
    text = path.read_text(encoding="utf-8").strip()
    try:
        return json.loads(text)
    except json.JSONDecodeError:
        # Strip code fences line-by-line.
        stripped = "\n".join(
            line for line in text.splitlines() if not line.strip().startswith("```")
        )
        return json.loads(stripped)


def validate_findings(doc: dict[str, Any]) -> tuple[str, list[dict[str, Any]]]:
    """Validate the review doc shape and normalize findings."""
    if not isinstance(doc, dict):
        fail("review doc is not an object")
    if not isinstance(doc.get("summary"), str):
        fail("review doc summary must be a string")
    summary = str(doc.get("summary") or "").strip()
    findings_raw = doc.get("findings") or []
    if not isinstance(findings_raw, list):
        fail("findings must be a list")
    out: list[dict[str, Any]] = []
    for f in findings_raw:
        if not isinstance(f, dict):
            fail("finding is not an object")
        sev = f.get("severity")
        if sev not in SEV_RANK:
            fail(f"invalid severity {sev!r}; expected one of {list(SEV_RANK)}")
        if not isinstance(f.get("title"), str) or not f["title"].strip():
            fail("finding missing non-empty title")
        if not isinstance(f.get("message"), str) or not f["message"].strip():
            fail("finding missing non-empty message")
        confidence = f.get("confidence")
        if confidence is not None and confidence not in ("high", "medium", "low"):
            fail(f"invalid confidence {confidence!r}")
        normalized = {
            "severity": sev,
            "title": f["title"].strip(),
            "message": f["message"],
            "file": f.get("file"),
            "line": f.get("line"),
            "confidence": confidence,
            "contextBasis": f.get("contextBasis"),
            "suggestion": f.get("suggestion"),
        }
        if isinstance(normalized["file"], str) and normalized["file"].startswith("/"):
            normalized["file"] = normalized["file"].lstrip("/")
        evidence = f.get("evidence") or {}
        if evidence:
            normalized["evidence"] = {
                "changedLines": [
                    x for x in (evidence.get("changed_lines") or []) if isinstance(x, int)
                ],
                "contextFilesRead": [
                    x for x in (evidence.get("context_files_read") or []) if isinstance(x, str)
                ],
                "whyNewInThisPr": str(evidence.get("why_new_in_this_pr") or "").strip(),
                "whyNotIntentional": str(evidence.get("why_not_intentional") or "").strip(),
            }
        out.append(normalized)
    return summary, out


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


def comment_body(f: dict[str, Any], key: str, max_chars: int) -> str:
    parts = [f"**{SEV_LABEL[f['severity']]}** — {f['title']}"]
    if f.get("confidence"):
        parts.append(f"Confidence: {f['confidence']}")
    if f.get("contextBasis"):
        parts.append(f"Context basis: {f['contextBasis']}")
    parts.extend(["", truncate(f["message"], 5000)])
    evidence = f.get("evidence") or {}
    evidence_lines: list[str] = []
    if evidence.get("whyNewInThisPr"):
        evidence_lines.append(f"Why in this PR: {evidence['whyNewInThisPr']}")
    if evidence.get("whyNotIntentional"):
        evidence_lines.append(f"Why not intentional: {evidence['whyNotIntentional']}")
    if evidence.get("contextFilesRead"):
        evidence_lines.append(
            "Context read: " + ", ".join(evidence["contextFilesRead"][:10])
        )
    if evidence_lines:
        parts.extend(["", "**Evidence**", *evidence_lines])
    if f.get("suggestion"):
        parts.extend(["", "**Suggested change**", fence(f["suggestion"])])
    body = "\n".join(parts)
    body = truncate(body, max_chars - 64)
    body += f"\n\n<!-- {MARKER}:{key} -->"
    return body


def current_reviewer_id(client: AdoClient, pr: dict[str, Any]) -> str | None:
    me = client.connection_data().get("authenticatedUser") or {}
    me_id = me.get("id")
    me_name = (me.get("uniqueName") or "").lower()
    for r in pr.get("reviewers") or []:
        if me_id and r.get("id") == me_id:
            return r.get("id")
        if me_name and (r.get("uniqueName") or "").lower() == me_name:
            return r.get("id")
    return None


def command_post_findings(args: argparse.Namespace) -> int:
    """Post findings to ADO. Idempotent: skips findings already present."""
    from auto_pr_reviewer.ado.diff_mapper import DiffLineMapper  # local import

    client = AdoClient(args.org, args.project, args.repo)
    doc = extract_json(Path(args.findings))
    summary, findings = validate_findings(doc)
    parsed_count = len(findings)

    # Apply POST_MIN_SEVERITY, drop_low_confidence, REQUIRE_CONTEXT_FOR, MAX_FINDINGS.
    post_min = os.getenv("POST_MIN_SEVERITY", "minor")
    if post_min not in SEV_RANK:
        fail(f"POST_MIN_SEVERITY must be one of: {list(SEV_RANK)}")
    drop_low = is_true(os.getenv("DROP_LOW_CONFIDENCE"))
    require_context_for_raw = os.getenv("REQUIRE_CONTEXT_FOR", "")
    require_context_for = {
        s.strip() for s in require_context_for_raw.split(",") if s.strip()
    } - {""}
    if require_context_for - SEV_RANK.keys():
        fail(f"REQUIRE_CONTEXT_FOR contains invalid severity(s): {require_context_for - SEV_RANK.keys()}")
    findings = [
        f for f in findings
        if (post_min == "none" or SEV_RANK[f["severity"]] >= SEV_RANK[post_min])
        and not (drop_low and f.get("confidence") == "low")
    ]
    if require_context_for:
        kept: list[dict[str, Any]] = []
        for f in findings:
            if f["severity"] in require_context_for:
                ctx_files = (f.get("evidence") or {}).get("contextFilesRead") or []
                ctx_basis = f.get("contextBasis")
                if not ctx_files and ctx_basis not in {"surrounding-code-read", "full-module-review"}:
                    log(
                        f"dropped finding '{f['title']}' ({f['severity']}): "
                        f"REQUIRE_CONTEXT_FOR={require_context_for_raw} but no context files read"
                    )
                    continue
            kept.append(f)
        findings = kept
    max_findings_raw = os.getenv("MAX_FINDINGS")
    max_findings: int | None = None
    if max_findings_raw:
        try:
            max_findings = int(max_findings_raw)
        except ValueError:
            fail(f"MAX_FINDINGS must be an integer, got {max_findings_raw!r}")
        if max_findings is not None and max_findings < 0:
            fail("MAX_FINDINGS must be non-negative")
    if max_findings is not None and len(findings) > max_findings:
        findings = sorted(findings, key=lambda f: SEV_RANK[f["severity"]], reverse=True)[:max_findings]
        log(f"capped findings MAX_FINDINGS={max_findings}")

    # Idempotency: scan existing threads for bot markers and skip.
    pr = client.get_pr(args.pr)
    existing = existing_bot_markers(client.get_threads(args.pr))

    diff_text = ""
    diff_path = Path(args.out).parent / "diff.patch"
    if diff_path.exists():
        diff_text = diff_path.read_text(encoding="utf-8")
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
    for f in findings:
        key = key_of(f)
        if key in existing:
            result["skipped"] += 1
            result["skipped_reasons"]["duplicate"] += 1
            log(f"skipping duplicate finding '{f['title']}' (key={key})")
            continue
        thread_body: dict[str, Any] = {"comments": [{"content": comment_body(f, key, 20000), "commentType": "text"}], "status": "active"}
        # Try line-anchored first.
        from auto_pr_reviewer.ado.diff_mapper import (
            map_file_line_to_diff_position,
            map_file_to_fallback,
        )
        ctx = None
        if f.get("file"):
            ctx = map_file_line_to_diff_position(f.get("file"), f.get("line"), mapper=mapper)
        if ctx is not None:
            thread_body["threadContext"] = ctx.to_thread_context()
        elif f.get("file") and (fb := map_file_to_fallback(f.get("file"), mapper=mapper)) is not None:
            thread_body["threadContext"] = fb.to_thread_context()
            result["skipped_reasons"]["file_fallback"] += 1
        else:
            result["skipped_reasons"]["no_line_mapping"] += 1
        resp = client.create_thread(args.pr, thread_body)
        result["created"] += 1
        result["comments"].append({"key": key, "threadId": (resp or {}).get("id"), "title": f["title"], "severity": f["severity"]})

    # Vote on the PR if configured.
    vote_waiting_on = os.getenv("VOTE_WAITING_ON", "none")
    if vote_waiting_on != "none":
        if vote_waiting_on not in SEV_RANK:
            fail(f"VOTE_WAITING_ON must be one of: {list(SEV_RANK)}")
        threshold = SEV_RANK[vote_waiting_on]
        if any(SEV_RANK[f["severity"]] >= threshold for f in findings):
            reviewer_id = current_reviewer_id(client, pr)
            if reviewer_id:
                client.vote(args.pr, reviewer_id, VOTE_WAITING)
                result["vote"] = {"reviewer_id": reviewer_id, "value": VOTE_WAITING}
                result["votedWaitingForAuthor"] = True

    fail_on = os.getenv("FAIL_ON", "none")
    if fail_on != "none" and any(SEV_RANK[f["severity"]] >= SEV_RANK.get(fail_on, 99) for f in findings):
        log(f"FAIL_ON={fail_on} threshold met; exiting 1")
        result["failOnTriggered"] = True
        write_json(out, result)
        return 1

    write_json(out, result)
    log(f"parsed {parsed_count} finding(s); {len(findings)} accepted for posting; skipped {result['skipped']} already-present finding(s)")
    return 0


def is_true(value: str | None) -> bool:
    return (value or "").lower() in {"1", "true", "yes", "on"}


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(description="Azure DevOps PR review helper")
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
    return int(args.func(args))


if __name__ == "__main__":
    raise SystemExit(main())
