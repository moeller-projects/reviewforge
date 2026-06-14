"""Azure DevOps integration subpackage."""
from __future__ import annotations

from .client import (
    AdoClient,
    call_helper,
    get_pr,
    parse_pr_url,
    resolve_branches,
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
    "call_helper",
    "dedupe_key",
    "existing_bot_markers",
    "get_pr",
    "map_file_line_to_diff_position",
    "parse_pr_url",
    "resolve_branches",
    "should_post",
]
