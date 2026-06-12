#!/usr/bin/env python3
"""Azure DevOps PR review integration helpers.

Direct REST replacement for the previous MCP-backed posting path and shell-heavy
context fetching.
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

SEV_RANK = {"nit": 1, "minor": 2, "major": 3, "blocker": 4}
SEV_LABEL = {"blocker": "🔴 blocker", "major": "🟠 major", "minor": "🟡 minor", "nit": "⚪ nit"}
VOTE_WAITING = -5
MARKER = "prb"


def log(message: str) -> None:
    print(f"[ado] {message}", file=sys.stderr)


def fail(message: str, code: int = 1) -> None:
    print(f"[ado][ERROR] {message}", file=sys.stderr)
    raise SystemExit(code)


def token() -> str:
    value = os.getenv("ADO_AUTH_TOKEN") or os.getenv("ADO_MCP_AUTH_TOKEN")
    if not value:
        fail("ADO_AUTH_TOKEN or ADO_MCP_AUTH_TOKEN is required", 2)
    return value


def normalize_org(org: str) -> tuple[str, str]:
    org = org.strip().rstrip("/")
    if org.startswith("https://"):
        if "dev.azure.com/" in org:
            short = org.split("dev.azure.com/", 1)[1].split("/", 1)[0]
            return org, short
        host = urllib.parse.urlparse(org).hostname or ""
        if host.endswith(".visualstudio.com"):
            return org, host.split(".", 1)[0]
        fail(f"Could not derive organization name from URL: {org}")
    return f"https://dev.azure.com/{org}", org


def enc(value: str | int) -> str:
    return urllib.parse.quote(str(value), safe="")


class AdoClient:
    def __init__(self, org: str, project: str, repo: str):
        self.org_url, self.org_name = normalize_org(org)
        self.project = project
        self.repo = repo
        self.token = token()
        self.base = f"{self.org_url}/{enc(project)}"

    def request(self, method: str, url: str, body: Any | None = None) -> Any:
        if url.startswith("https://"):
            full_url = url
        else:
            if "api-version=" in url:
                full_url = f"{self.base}{url}"
            else:
                sep = "&" if "?" in url else "?"
                full_url = f"{self.base}{url}{sep}api-version=7.1"

        data = None
        headers = {
            "Authorization": f"Bearer {self.token}",
            "Accept": "application/json",
        }
        if body is not None:
            data = json.dumps(body).encode("utf-8")
            headers["Content-Type"] = "application/json"

        req = urllib.request.Request(full_url, data=data, headers=headers, method=method)
        try:
            with urllib.request.urlopen(req, timeout=60) as resp:
                raw = resp.read().decode("utf-8")
                return json.loads(raw) if raw else None
        except urllib.error.HTTPError as e:
            detail = e.read().decode("utf-8", errors="replace")
            fail(f"{method} {full_url} failed with HTTP {e.code}: {detail}")
        except urllib.error.URLError as e:
            fail(f"{method} {full_url} failed: {e}")

    def get(self, path: str) -> Any:
        return self.request("GET", path)

    def post(self, path: str, body: Any) -> Any:
        return self.request("POST", path, body)

    def put(self, path: str, body: Any) -> Any:
        return self.request("PUT", path, body)

    def pr_path(self, pr_id: int, suffix: str = "") -> str:
        return f"/_apis/git/repositories/{enc(self.repo)}/pullRequests/{enc(pr_id)}{suffix}"

    def get_pr(self, pr_id: int) -> dict[str, Any]:
        return self.get(self.pr_path(pr_id))

    def get_threads(self, pr_id: int) -> list[dict[str, Any]]:
        return self.get(self.pr_path(pr_id, "/threads")).get("value", [])

    def create_thread(self, pr_id: int, body: dict[str, Any]) -> Any:
        return self.post(self.pr_path(pr_id, "/threads"), body)

    def connection_data(self) -> dict[str, Any]:
        url = f"{self.org_url}/_apis/connectionData?connectOptions=1&lastChangeId=-1&lastChangeId64=-1&api-version=7.1-preview.1"
        return self.get(url)

    def vote(self, pr_id: int, reviewer_id: str, vote: int) -> Any:
        path = self.pr_path(pr_id, f"/reviewers/{enc(reviewer_id)}")
        return self.put(path, {"vote": vote})


def write_json(path: Path, value: Any) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(value, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")


def simplify_thread(thread: dict[str, Any]) -> dict[str, Any]:
    comments = thread.get("comments") or []
    first = comments[0] if comments else {}
    ctx = thread.get("threadContext") or {}
    return {
        "id": thread.get("id"),
        "status": thread.get("status"),
        "filePath": ctx.get("filePath"),
        "line": ((ctx.get("rightFileStart") or {}).get("line")),
        "firstComment": first.get("content") or "",
        "author": ((first.get("author") or {}).get("displayName")) or "unknown",
    }


def fetch_work_items(client: AdoClient, pr: dict[str, Any]) -> tuple[list[dict[str, Any]], list[dict[str, Any]]]:
    ids = [int(ref["id"]) for ref in pr.get("workItemRefs") or [] if str(ref.get("id", "")).isdigit()]
    if not ids:
        return [], []

    batch = client.post(
        "/_apis/wit/workitemsbatch",
        {
            "ids": ids,
            "fields": [
                "System.Title",
                "System.Description",
                "Microsoft.VSTS.Common.AcceptanceCriteria",
                "System.WorkItemType",
                "System.State",
            ],
        },
    )

    work_items = []
    for item in batch.get("value", []):
        fields = item.get("fields") or {}
        work_items.append(
            {
                "id": item.get("id"),
                "type": fields.get("System.WorkItemType") or "Unknown",
                "title": fields.get("System.Title") or "(untitled)",
                "state": fields.get("System.State") or "",
                "description": fields.get("System.Description") or "(none)",
                "acceptanceCriteria": fields.get("Microsoft.VSTS.Common.AcceptanceCriteria") or "(none)",
            }
        )

    comments_by_item = []
    for wid in ids:
        raw = client.get(f"/_apis/wit/workItems/{enc(wid)}/comments?api-version=7.1-preview.4")
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


def command_fetch_context(args: argparse.Namespace) -> int:
    client = AdoClient(args.org, args.project, args.repo)
    out = Path(args.out)

    log(f"fetching PR #{args.pr} context")
    pr = client.get_pr(args.pr)
    work_items, work_item_comments = fetch_work_items(client, pr)
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


def extract_json(path: Path) -> dict[str, Any]:
    return json.loads(path.read_text(encoding="utf-8").strip())


def validate_findings(doc: dict[str, Any]) -> tuple[str, list[dict[str, Any]]]:
    if not isinstance(doc, dict):
        fail("findings document is not an object")
    summary = doc.get("summary")
    if not isinstance(summary, str):
        fail("findings.summary must be a string")
    raw_findings = doc.get("findings")
    if not isinstance(raw_findings, list):
        fail("findings.findings must be an array")

    findings = []
    for i, f in enumerate(raw_findings):
        if not isinstance(f, dict):
            fail(f"finding[{i}] is not an object")
        sev = str(f.get("severity") or "").lower()
        if sev not in SEV_RANK:
            fail(f"finding[{i}].severity invalid: {f.get('severity')}")
        msg = f.get("message")
        if not isinstance(msg, str) or not msg.strip():
            fail(f"finding[{i}].message missing")
        confidence = str(f.get("confidence") or "").lower()
        if confidence and confidence not in {"high", "medium", "low"}:
            fail(f"finding[{i}].confidence invalid: {f.get('confidence')}")
        evidence = f.get("evidence") if isinstance(f.get("evidence"), dict) else None
        normalized = {
            "file": str(f.get("file")).lstrip("/") if isinstance(f.get("file"), str) and f.get("file") else None,
            "line": f.get("line") if isinstance(f.get("line"), int) and f.get("line") > 0 else None,
            "severity": sev,
            "title": str(f.get("title") or "Review finding").strip(),
            "message": msg.strip(),
            "confidence": confidence or None,
            "suggestion": f.get("suggestion").strip() if isinstance(f.get("suggestion"), str) and f.get("suggestion").strip() else None,
        }
        if evidence:
            normalized["evidence"] = {
                "changedLines": [x for x in evidence.get("changed_lines", []) if isinstance(x, int)],
                "contextFilesRead": [x for x in evidence.get("context_files_read", []) if isinstance(x, str)],
                "whyNewInThisPr": str(evidence.get("why_new_in_this_pr") or "").strip(),
                "whyNotIntentional": str(evidence.get("why_not_intentional") or "").strip(),
            }
        findings.append(normalized)
    return summary.strip(), findings


def key_of(f: dict[str, Any]) -> str:
    raw = f"{f.get('file') or ''}|{f.get('line') or ''}|{f['severity']}|{f['title']}"
    return hashlib.sha1(raw.encode("utf-8")).hexdigest()[:12]


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
    parts.extend(["", truncate(f["message"], 5000)])

    evidence = f.get("evidence") or {}
    evidence_lines = []
    if evidence.get("whyNewInThisPr"):
        evidence_lines.append(f"Why this is new: {truncate(evidence['whyNewInThisPr'], 800)}")
    if evidence.get("whyNotIntentional"):
        evidence_lines.append(f"Why this does not look intentional: {truncate(evidence['whyNotIntentional'], 800)}")
    if evidence.get("contextFilesRead"):
        evidence_lines.append("Context checked: " + ", ".join(evidence["contextFilesRead"][:6]))
    if evidence_lines:
        parts.extend(["", "Evidence:", *evidence_lines])

    if f.get("suggestion"):
        parts.extend(["", "Suggestion:", fence(truncate(f["suggestion"], 5000))])

    parts.extend(["", f"<sub>{MARKER}:{key}</sub>"])
    return truncate("\n".join(parts), max_chars)


def worst_rank(findings: list[dict[str, Any]]) -> int:
    return max([SEV_RANK.get(f.get("severity"), 0) for f in findings] + [0])


def should_threshold(findings: list[dict[str, Any]], threshold: str) -> bool:
    if threshold == "none":
        return False
    return worst_rank(findings) >= SEV_RANK.get(threshold, 999)


def current_reviewer_id(client: AdoClient, pr: dict[str, Any]) -> str | None:
    user = (client.connection_data().get("authenticatedUser") or {})
    uid = str(user.get("id") or "").lower()
    unique = str(user.get("uniqueName") or "").lower()
    for reviewer in pr.get("reviewers") or []:
        rid = str(reviewer.get("id") or "")
        runique = str(reviewer.get("uniqueName") or "").lower()
        if (uid and rid.lower() == uid) or (unique and runique == unique):
            return rid
    return None


def command_post_findings(args: argparse.Namespace) -> int:
    client = AdoClient(args.org, args.project, args.repo)
    post_min = os.getenv("POST_MIN_SEVERITY", "major").lower()
    vote_waiting_on = os.getenv("VOTE_WAITING_ON", "major").lower()
    fail_on = os.getenv("FAIL_ON", "none").lower()
    drop_low = os.getenv("DROP_LOW_CONFIDENCE", "true").lower() != "false"
    max_comment_chars = int(os.getenv("MAX_COMMENT_CHARS", "12000"))

    for name, value in [("POST_MIN_SEVERITY", post_min), ("VOTE_WAITING_ON", vote_waiting_on), ("FAIL_ON", fail_on)]:
        if value != "none" and value not in SEV_RANK:
            fail(f"{name} must be one of: none, nit, minor, major, blocker")

    summary, findings = validate_findings(extract_json(Path(args.findings)))
    parsed_count = len(findings)
    findings = [
        f
        for f in findings
        if (post_min == "none" or SEV_RANK[f["severity"]] >= SEV_RANK[post_min])
        and not (drop_low and f.get("confidence") == "low")
    ]
    log(f"parsed {parsed_count} finding(s); {len(findings)} accepted for posting")

    pr = client.get_pr(args.pr)
    threads = client.get_threads(args.pr)
    existing_text = "\n".join((c.get("content") or "") for t in threads for c in (t.get("comments") or []))

    created = 0
    skipped = 0
    posted = []
    for f in findings:
        key = key_of(f)
        if f"{MARKER}:{key}" in existing_text:
            skipped += 1
            posted.append({"key": key, "finding": f, "action": "skipped-existing"})
            continue

        body = {
            "comments": [{"parentCommentId": 0, "content": comment_body(f, key, max_comment_chars), "commentType": 1}],
            "status": 1,
        }
        if f.get("file") and f.get("line"):
            body["threadContext"] = {
                "filePath": "/" + f["file"].lstrip("/"),
                "rightFileStart": {"line": f["line"], "offset": 1},
                "rightFileEnd": {"line": f["line"], "offset": 1},
            }
        response = client.create_thread(args.pr, body)
        created += 1
        posted.append({"key": key, "finding": f, "action": "created", "threadId": (response or {}).get("id")})

    voted = False
    vote_error = None
    if should_threshold(findings, vote_waiting_on):
        reviewer_id = current_reviewer_id(client, pr)
        if reviewer_id:
            try:
                client.vote(args.pr, reviewer_id, VOTE_WAITING)
                voted = True
                log(f"voted waiting-for-author on PR #{args.pr}")
            except SystemExit:
                raise
            except Exception as e:  # defensive; urllib failures use fail()
                vote_error = str(e)
        else:
            vote_error = "current authenticated user is not a reviewer on this PR"
            log(f"could not vote: {vote_error}")

    result = {
        "summary": summary,
        "parsed": parsed_count,
        "accepted": len(findings),
        "created": created,
        "skipped": skipped,
        "votedWaitingForAuthor": voted,
        "voteError": vote_error,
        "posted": posted,
    }
    write_json(Path(args.out), result)
    log(f"created {created}, skipped {skipped} already-present finding(s)")

    if should_threshold(findings, fail_on):
        log(f"FAIL_ON={fail_on} threshold met; exiting 1")
        return 1
    return 0


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
    return int(args.func(args) or 0)


if __name__ == "__main__":
    raise SystemExit(main())
