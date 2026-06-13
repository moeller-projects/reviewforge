from dataclasses import dataclass
from typing import Any

@dataclass(frozen=True)
class PrIdentity:
    org: str
    project: str
    repo: str
    pr_id: str

JsonObject = dict[str, Any]
