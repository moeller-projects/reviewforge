"""Shared ADO dataclasses and type aliases."""
from __future__ import annotations

from dataclasses import dataclass
from typing import Any

JsonObject = dict[str, Any]


@dataclass(frozen=True)
class PrIdentity:
    """Resolved PR coordinates: org, project, repo, id."""

    org: str
    project: str
    repo: str
    pr_id: str


__all__ = ["JsonObject", "PrIdentity"]
