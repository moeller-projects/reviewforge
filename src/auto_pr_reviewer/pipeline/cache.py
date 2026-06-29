"""Tiny on-disk cache for expensive deterministic stage outputs."""
from __future__ import annotations

from hashlib import sha256
from pathlib import Path
import json
from typing import Any


def stage_cache_root(cfg, stage: str) -> Path:
    root = Path(cfg.review_artifact_root) / f"pr-{cfg.pr_id}" / "cache" / stage
    root.mkdir(parents=True, exist_ok=True)
    return root


def cache_key(parts: list[Any]) -> str:
    data = json.dumps(parts, sort_keys=True, ensure_ascii=False, default=str, separators=(",", ":"))
    return sha256(data.encode("utf-8")).hexdigest()[:24]


def cache_path(cfg, stage: str, key: str) -> Path:
    return stage_cache_root(cfg, stage) / f"{key}.json"


def load_cached_json(cfg, stage: str, key: str) -> dict[str, Any] | None:
    path = cache_path(cfg, stage, key)
    if not path.exists():
        return None
    return json.loads(path.read_text(encoding="utf-8"))


def store_cached_json(cfg, stage: str, key: str, payload: dict[str, Any]) -> Path:
    path = cache_path(cfg, stage, key)
    path.write_text(json.dumps(payload, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    return path
