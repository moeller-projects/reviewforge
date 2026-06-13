from __future__ import annotations

import json, re, subprocess, sys, urllib.parse, urllib.request
from pathlib import Path
from typing import Any

from config import Config


def parse_pr_url(value: str) -> tuple[str, str, str, str]:
    patterns = [r"dev\.azure\.com/([^/]+)/([^/]+)/_git/([^/]+)/pullrequest/(\d+)", r"://([^/.]+)\.visualstudio\.com/([^/]+)/_git/([^/]+)/pullrequest/(\d+)"]
    for pattern in patterns:
        match = re.search(pattern, value)
        if match:
            return tuple(urllib.parse.unquote(x) for x in match.groups())  # type: ignore[return-value]
    raise SystemExit("[review][ERROR] Could not parse PR_URL")


def get_pr(cfg: Config) -> dict[str, Any]:
    base = f"https://dev.azure.com/{urllib.parse.quote(cfg.ado_org)}/{urllib.parse.quote(cfg.ado_project)}"
    url = f"{base}/_apis/git/repositories/{urllib.parse.quote(cfg.ado_repo_id, safe='')}/pullRequests/{cfg.pr_id}?api-version=7.1"
    req = urllib.request.Request(url, headers={"Authorization": f"Bearer {cfg.ado_token}", "Accept": "application/json"})
    with urllib.request.urlopen(req, timeout=60) as response:
        return json.loads(response.read().decode("utf-8"))


def resolve_branches(cfg: Config) -> tuple[str, str]:
    source, target = cfg.source_branch, cfg.target_branch
    if not source or not target:
        data = get_pr(cfg)
        source = source or data.get("sourceRefName") or ""
        target = target or data.get("targetRefName") or ""
    if not source or not target:
        raise SystemExit("[review][ERROR] could not resolve source/target branch from API")
    return source.removeprefix("refs/heads/"), target.removeprefix("refs/heads/")


def call_helper(cfg: Config, command: str, artifact_dir: Path, *, findings: Path | None = None) -> None:
    helper = Path(__file__).resolve().parents[2] / "ado_review.py"
    args = [sys.executable, str(helper), command, "--org", cfg.ado_org, "--project", cfg.ado_project, "--repo", cfg.ado_repo_id, "--pr", cfg.pr_id]
    if command == "fetch-context":
        args += ["--out", str(artifact_dir)]
    else:
        assert findings is not None
        args += ["--findings", str(findings), "--out", str(artifact_dir / "posted-findings.json")]
    cp = subprocess.run(args, stdout=subprocess.PIPE, stderr=subprocess.PIPE)
    if cp.stderr:
        for line in cp.stderr.decode(errors='replace').splitlines():
            print(f"[review][ado {command}] {line}", file=sys.stderr)
    if cp.returncode:
        raise SystemExit(f"[review][ERROR] ado_review {command} failed: {cp.stderr.decode(errors='replace')}")
