"""Lightweight Azure DevOps REST client used by the reviewer.

Authentication is a bearer token (typically ``System.AccessToken`` in
Pipelines). The reviewer only needs GET on PRs/threads and POST on
``/threads`` plus PUT on the reviewer vote — no other endpoints are used.
"""
from __future__ import annotations

import json
import os
import re
import subprocess
import random
import time
import sys
import urllib.parse
import urllib.request
from pathlib import Path
from typing import Any

from ..config import Config
from ..exceptions import AdoApiError
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
    raise AdoApiError("[review][ERROR] Could not parse PR_URL", details={"url": value})


def normalize_ado_segment(value: str, name: str) -> str:
    """Validate and normalize an ADO org/project/repo segment.

    Rules (preserved from the legacy PowerShell helper):

    * Must be non-empty after stripping.
    * Must not contain ``://`` (i.e. must be the short name, not a URL).
    * Must not contain CR/LF (defense against header-injection style attacks).
    * Strips a trailing ``/``.
    """
    if value is None:
        raise ValueError(f"{name} is required")
    normalized = value.strip()
    if not normalized:
        raise ValueError(f"{name} is required")
    if "://" in normalized:
        raise ValueError(
            f"{name} must be the short Azure DevOps name, not a URL: {value!r}"
        )
    if re.search(r"[\r\n]", normalized):
        raise ValueError(
            f"{name} must not contain line breaks: {value!r}"
        )
    return normalized.rstrip("/")


def normalize_branch_name(branch: str) -> str:
    """Strip a leading ``refs/heads/`` prefix from a branch name."""
    if not branch:
        return branch
    return re.sub(r"^refs/heads/", "", branch)


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
        value = os.environ.get(name)
        if value:
            return value
    raise AdoApiError(
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
        raise AdoApiError(
            f"[review][ERROR] Could not derive organization name from URL: {org}",
            details={"organization": org},
        )
    if "/" in raw or "." in raw:
        raise AdoApiError(
            f"[review][ERROR] Could not derive organization name from URL: {org}",
            details={"organization": org},
        )
    return f"https://dev.azure.com/{raw}", raw


class AdoClient:
    """Minimal Azure DevOps REST client for the reviewer."""

    def __init__(
        self,
        org: str,
        project: str,
        repo: str,
        token: str | None = None,
        *,
        retry_attempts: int = 3,
        retry_base_delay: float = 1.0,
        retry_cap_delay: float = 30.0,
        retry_budget_secs: float = 90.0,
        sleeper: Any = time.sleep,
        random_fn: Any = random.random,
        monotonic: Any = time.monotonic,
    ):
        self.org_url, self.org_name = _normalize_org(org)
        self.project = project
        self.repo = repo
        self.base = f"{self.org_url}/{urllib.parse.quote(project)}"
        self.token = token or resolve_token()
        self.retry_attempts = max(1, retry_attempts)
        self.retry_base_delay = max(0.0, retry_base_delay)
        self.retry_cap_delay = max(0.0, retry_cap_delay)
        self.retry_budget_secs = max(0.0, retry_budget_secs)
        self._sleep = sleeper
        self._random = random_fn
        self._monotonic = monotonic

    # ----- low-level --------------------------------------------------------

    def _retry_delay(self, attempt: int, retry_after: str | None) -> float:
        if retry_after:
            try:
                return min(self.retry_cap_delay, max(0.0, float(retry_after)))
            except ValueError:
                pass
        delay = min(self.retry_cap_delay, self.retry_base_delay * (2 ** (attempt - 1)))
        return delay * (0.5 + self._random() / 2)

    def _request(self, method: str, url: str, body: Any | None = None) -> dict[str, Any]:
        data = None if body is None else json.dumps(body).encode("utf-8")
        req = urllib.request.Request(
            url,
            data=data,
            method=method,
            headers={
                "Authorization": f"Bearer {self.token}",
                "Accept": "application/json; api-version=7.0",
                **({"Content-Type": "application/json"} if data is not None else {}),
            },
        )
        started = self._monotonic()
        for attempt in range(1, self.retry_attempts + 1):
            try:
                with urllib.request.urlopen(req, timeout=60) as response:  # nosec - trusted URL
                    raw = response.read().decode("utf-8")
                    return json.loads(raw) if raw else {}
            except urllib.error.HTTPError as exc:
                error_body = ""
                try:
                    error_body = exc.read().decode("utf-8", errors="replace")
                except Exception:
                    pass
                retryable = method not in {"POST", "PUT"} and exc.code in {429, 500, 502, 503, 504}
                error = AdoApiError(
                    f"[review][ERROR] ADO API {method} {url} returned {exc.code} {exc.reason}",
                    details={"method": method, "url": url, "status_code": exc.code, "response_body": error_body},
                )
                retry_after = exc.headers.get("Retry-After") if exc.headers else None
            except (urllib.error.URLError, TimeoutError) as exc:
                retryable = True
                retry_after = None
                error = AdoApiError(
                    f"[review][ERROR] ADO API {method} {url} failed: {exc.reason if isinstance(exc, urllib.error.URLError) else exc}",
                    details={"method": method, "url": url},
                )
            if not retryable or attempt == self.retry_attempts:
                raise error
            delay = self._retry_delay(attempt, retry_after)
            if self._monotonic() - started + delay > self.retry_budget_secs:
                raise error
            print(
                f"[review][ado] attempt {attempt}/{self.retry_attempts} failed: {error.message}; retrying in {delay:g}s",
                file=sys.stderr,
            )
            self._sleep(delay)

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

    def get_commits(self, pr_id: int | str) -> list[dict[str, Any]]:
        """Return commits associated with a pull request, newest first."""
        return self._request(
            "GET", self.base + self.pr_path(pr_id, "/commits?api-version=7.1")
        ).get("value", [])

    def create_thread(self, pr_id: int | str, body: dict[str, Any]) -> Any:
        return self._request("POST", self.base + self.pr_path(pr_id, "/threads"), body)

    def add_comment(
        self, pr_id: int | str, thread_id: int | str, content: str
    ) -> Any:
        """Append a text comment to an existing thread.

        Used by the stale-comment reconciliation pass: when a finding
        posted in a prior run no longer anchors to a line that exists
        in the current diff, the bot appends a comment to the existing
        thread so reviewers know the finding is stale rather than
        silently leaving an outdated inline comment on the PR.
        """
        return self._request(
            "POST",
            self.base + self.pr_path(pr_id, f"/threads/{thread_id}/comments"),
            {"content": content, "commentType": "text"},
        )

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
    client = AdoClient(
        cfg.ado_org, cfg.ado_project, cfg.ado_repo_id, token=cfg.ado_token,
        retry_attempts=cfg.ado_retry_attempts, retry_base_delay=cfg.ado_retry_base_delay,
        retry_cap_delay=cfg.ado_retry_cap_delay, retry_budget_secs=cfg.ado_retry_budget_secs,
    )
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
        raise AdoApiError(
            "[review][ERROR] could not resolve source/target branch from API",
            details={"source": source, "target": target},
        )
    return source.removeprefix("refs/heads/"), target.removeprefix("refs/heads/")


def list_active_pull_requests(
    cfg: Config,
    *,
    project: str | None = None,
    target_branches: list[str] | None = None,
    max_results: int = 0,
) -> list[dict[str, Any]]:
    """List active PRs for a project, with optional branch / cap filters.

    Paginates ADO's ``/pullRequests?searchCriteria.status=active`` endpoint.
    ``target_branches`` may contain either short branch names (``main``) or
    full ref names (``refs/heads/main``). Matching is exact after stripping
    the ``refs/heads/`` prefix.

    Returns a list of PR dicts (the same shape ``AdoClient.get_pr`` returns
    plus the project name).
    """
    project_name = project or cfg.ado_project
    project_name = normalize_ado_segment(project_name, "ADO project")
    client = AdoClient(
        cfg.ado_org, project_name, cfg.ado_repo_id, token=cfg.ado_token,
        retry_attempts=cfg.ado_retry_attempts, retry_base_delay=cfg.ado_retry_base_delay,
        retry_cap_delay=cfg.ado_retry_cap_delay, retry_budget_secs=cfg.ado_retry_budget_secs,
    )
    target_set: set[str] | None = None
    if target_branches:
        target_set = {normalize_branch_name(b) for b in target_branches if b}
        target_set = {b for b in target_set if b}

    page_size = 100
    skip = 0
    out: list[dict[str, Any]] = []
    while True:
        encoded_repo = urllib.parse.quote(client.repo, safe="")
        url = (
            f"{client.base}/_apis/git/repositories/{encoded_repo}/pullRequests"
            f"?searchCriteria.status=active&api-version=7.0"
            f"&$top={page_size}&$skip={skip}"
        )
        page = client._request("GET", url).get("value", [])  # noqa: SLF001 — internal
        if not page:
            break
        for pr in page:
            target_ref = pr.get("targetRefName") or ""
            if target_set is not None:
                short = normalize_branch_name(target_ref)
                if short not in target_set:
                    continue
            pr_out = dict(pr)
            pr_out["project"] = project_name
            out.append(pr_out)
            if max_results and len(out) >= max_results:
                return out
        if len(page) < page_size:
            break
        skip += page_size
    return out


def call_helper(
    cfg: Config,
    command: str,
    artifact_dir: Path,
    *,
    findings: Path | None = None,
) -> None:
    """Invoke the package's isolated ADO CLI as a subprocess.

    The subprocess keeps ADO side effects isolated while avoiding a
    repository-relative helper script.
    """
    args = [
        sys.executable,
        "-m",
        "reviewforge.ado.cli",
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
        stderr = cp.stderr.decode(errors="replace")
        raise AdoApiError(
            f"[review][ERROR] ADO CLI {command} failed: {stderr}",
            details={"command": command, "returncode": cp.returncode, "stderr": stderr},
        )


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
