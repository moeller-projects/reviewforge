"""Azure DevOps integration subpackage."""
from __future__ import annotations

from .client import (
    AdoClient,
    get_pr,
    normalize_ado_segment,
    normalize_branch_name,
    parse_pr_url,
    resolve_branches,
    resolve_token,
)
from .models import JsonObject, PrIdentity
from .posting import (
    BotMarkers,
    DedupeKey,
    dedupe_key,
    existing_bot_markers,
    should_post,
)
from .diff_mapper import (
    AdoThreadContext,
    DiffLineMapper,
    map_file_line_to_diff_position,
)

__all__ = [
    "AdoClient",
    "AdoThreadContext",
    "BotMarkers",
    "DedupeKey",
    "DiffLineMapper",
    "JsonObject",
    "PrIdentity",
    "dedupe_key",
    "existing_bot_markers",
    "get_pr",
    "map_file_line_to_diff_position",
    "normalize_ado_segment",
    "normalize_branch_name",
    "parse_pr_url",
    "resolve_branches",
    "resolve_token",
    "should_post",
]
