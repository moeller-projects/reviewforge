"""Lightweight Azure DevOps REST client used by the reviewer.

Authentication is a bearer token (typically ``System.AccessToken`` in
Pipelines). The reviewer only needs GET on PRs/threads and POST on
``/threads`` plus PUT on the reviewer vote — no other endpoints are used.
"""
from __future__ import annotations

import json
import re
import subprocess
import sys
import urllib.parse
import urllib.request
from pathlib import Path
from typing import Any

from ..config import Config
from .models import PrIdentity


# ---------------------------------------------------------------------------
# URL parsing
# ---------------------------------------------------------------------------

_PR_URL_PATTERNS = (
    r"dev\.azure\.com/([^/]+)/([^/]+)/_git/([^/]+)/pullrequest/(\d+)",
    r"://([^/.]+)\.visualstudio\.com/([^/]+)/_git/([^/]+)/pullrequest/(\d+)",
)


def parse_pr_url(value: str) -> tuple[str, str, str, str]:
    """Parse an ADO PR URL into ``(org, project, repo, pr_id)``.

    Accepts both ``dev.azure.com`` and ``<org>.visualstudio.com`` URLs.
    """
    for pattern in _PR_URL_PATTERNS:
        match = re.search(pattern, value)
        if match:
            return tuple(urllib.parse.unquote(x) for x in match.groups())  # type: ignore[return-value]
    raise SystemExit("[review][ERROR] Could not parse PR_URL")


def parse_pr_identity(value: str) -> PrIdentity:
    """Convenience wrapper that returns a :class:`PrIdentity`."""
    org, project, repo, pr_id = parse_pr_url(value)
    return PrIdentity(org=org, project=project, repo=repo, pr_id=pr_id)


# ---------------------------------------------------------------------------
# Token
# ---------------------------------------------------------------------------


def resolve_token() -> str:
    """Read the ADO bearer token from the environment.

    Accepts ``ADO_AUTH_TOKEN`` (preferred) and the legacy aliases
    ``ADO_MCP_AUTH_TOKEN`` / ``ADO_API_KEY``.
    """
    for name in ("ADO_AUTH_TOKEN", "ADO_MCP_AUTH_TOKEN", "ADO_API_KEY"):
        value = __import__("os").environ.get(name)
        if value:
            return value
    raise SystemExit(
        "[review][ERROR] ADO_AUTH_TOKEN (aliases: ADO_MCP_AUTH_TOKEN, ADO_API_KEY) required"
    )


# ---------------------------------------------------------------------------
# AdoClient
# ---------------------------------------------------------------------------


def _normalize_org(org: str) -> tuple[str, str]:
    """Return ``(org_url, short_name)`` from a raw org string or URL."""
    raw = org.strip().rstrip("/")
    if raw.startswith("https://"):
        if "dev.azure.com/" in raw:
            short = raw.split("dev.azure.com/", 1)[1].split("/", 1)[0]
            return raw, short
        host = urllib.parse.urlparse(raw).hostname or ""
        if host.endswith(".visualstudio.com"):
            return raw, host.split(".", 1)[0]
        raise SystemExit(f"[review][ERROR] Could not derive organization name from URL: {org}")
    if "/" in raw or "." in raw:
        raise SystemExit(f"[review][ERROR] Could not derive organization name from URL: {org}")
    return f"https://dev.azure.com/{raw}", raw


class AdoClient:
    """Minimal Azure DevOps REST client for the reviewer."""

    def __init__(self, org: str, project: str, repo: str, token: str | None = None):
        self.org_url, self.org_name = _normalize_org(org)
        self.project = project
        self.repo = repo
        self.base = f"{self.org_url}/{urllib.parse.quote(project)}"
        self.token = token or resolve_token()

    # ----- low-level --------------------------------------------------------

    def _request(self, method: str, url: str, body: Any | None = None) -> dict[str, Any]:
        data = None if body is None else json.dumps(body).encode("utf-8")
        req = urllib.request.Request(
            url,
            data=data,
            method=method,
            headers={
                "Authorization": f"Bearer {self.token}",
                "Accept": "application/json",
                **({"Content-Type": "application/json"} if data is not None else {}),
            },
        )
        with urllib.request.urlopen(req, timeout=60) as response:  # nosec - trusted URL
            raw = response.read().decode("utf-8")
            if not raw:
                return {}
            return json.loads(raw)

    # ----- paths ------------------------------------------------------------

    def pr_path(self, pr_id: int | str, suffix: str = "") -> str:
        repo = urllib.parse.quote(self.repo, safe="")
        pid = urllib.parse.quote(str(pr_id), safe="")
        return f"/_apis/git/repositories/{repo}/pullRequests/{pid}{suffix}"

    # ----- public API -------------------------------------------------------

    def get_pr(self, pr_id: int | str, *, include_work_item_refs: bool = False) -> dict[str, Any]:
        path = self.pr_path(pr_id)
        if include_work_item_refs:
            path += "?includeWorkItemRefs=true"
        return self._request("GET", self.base + path)

    def get_threads(self, pr_id: int | str) -> list[dict[str, Any]]:
        return self._request("GET", self.base + self.pr_path(pr_id, "/threads")).get("value", [])

    def create_thread(self, pr_id: int | str, body: dict[str, Any]) -> Any:
        return self._request("POST", self.base + self.pr_path(pr_id, "/threads"), body)

    def vote(self, pr_id: int | str, reviewer_id: str, vote: int) -> Any:
        return self._request(
            "PUT",
            self.base + self.pr_path(pr_id, f"/reviewers/{urllib.parse.quote(reviewer_id, safe='')}"),
            {"vote": vote},
        )

    def connection_data(self) -> dict[str, Any]:
        url = (
            f"{self.org_url}/_apis/connectionData"
            "?connectOptions=1&lastChangeId=-1&lastChangeId64=-1&api-version=7.1-preview.1"
        )
        return self._request("GET", url)

    def post(self, path: str, body: Any) -> Any:
        return self._request("POST", self.base + path, body)

    def put(self, path: str, body: Any) -> Any:
        return self._request("PUT", self.base + path, body)

    def get(self, path: str) -> Any:
        return self._request("GET", self.base + path)


# ---------------------------------------------------------------------------
# High-level helpers (formerly in client.py)
# ---------------------------------------------------------------------------


def get_pr(cfg: Config) -> dict[str, Any]:
    """Fetch a single PR via the REST API using :class:`Config`."""
    client = AdoClient(cfg.ado_org, cfg.ado_project, cfg.ado_repo_id, token=cfg.ado_token)
    return client.get_pr(cfg.pr_id)


def resolve_branches(cfg: Config) -> tuple[str, str]:
    """Return ``(source_short, target_short)`` for a PR.

    Uses cached values from the config when set; otherwise fetches the PR and
    extracts them from ``sourceRefName`` / ``targetRefName``.
    """
    source, target = cfg.source_branch, cfg.target_branch
    if not source or not target:
        data = get_pr(cfg)
        source = source or data.get("sourceRefName") or ""
        target = target or data.get("targetRefName") or ""
    if not source or not target:
        raise SystemExit("[review][ERROR] could not resolve source/target branch from API")
    return source.removeprefix("refs/heads/"), target.removeprefix("refs/heads/")


def call_helper(
    cfg: Config,
    command: str,
    artifact_dir: Path,
    *,
    findings: Path | None = None,
) -> None:
    """Invoke the legacy ``scripts/ado_review.py`` helper as a subprocess.

    Used to post findings to ADO without sharing the in-process state. The
    helper script is a thin wrapper that re-exports the new package's CLI.
    """
    helper = _find_helper_script()
    args = [
        sys.executable,
        str(helper),
        command,
        "--org",
        cfg.ado_org,
        "--project",
        cfg.ado_project,
        "--repo",
        cfg.ado_repo_id,
        "--pr",
        cfg.pr_id,
    ]
    if command == "fetch-context":
        args += ["--out", str(artifact_dir)]
    else:
        assert findings is not None
        args += ["--findings", str(findings), "--out", str(artifact_dir / "posted-findings.json")]
    cp = subprocess.run(args, stdout=subprocess.PIPE, stderr=subprocess.PIPE)
    if cp.stderr:
        for line in cp.stderr.decode(errors="replace").splitlines():
            print(f"[review][ado {command}] {line}", file=sys.stderr)
    if cp.returncode:
        raise SystemExit(
            f"[review][ERROR] ado_review {command} failed: {cp.stderr.decode(errors='replace')}"
        )


def _find_helper_script() -> Path:
    """Locate the legacy ``ado_review.py`` script relative to this package.

    Looks first for a ``scripts/ado_review.py`` next to ``src/``, then falls
    back to the package's own CLI module.
    """
    # In source tree: src/auto_pr_reviewer/ado/client.py → ../../scripts/ado_review.py
    here = Path(__file__).resolve()
    candidates = [
        here.parents[3] / "scripts" / "ado_review.py",
        here.parents[2] / "scripts" / "ado_review.py",
    ]
    for c in candidates:
        if c.exists():
            return c
    # Fallback: run the module's CLI directly via -m.
    return Path(sys.executable)  # placeholder; the subprocess will still need a script


__all__ = [
    "AdoClient",
    "PrIdentity",
    "call_helper",
    "get_pr",
    "parse_pr_identity",
    "parse_pr_url",
    "resolve_branches",
    "resolve_token",
]
