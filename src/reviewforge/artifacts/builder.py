"""Artifact IO helpers: JSON read/write and changed-file classification."""
from __future__ import annotations

from pathlib import Path
import json
import re
from typing import Any

_EXT_LANG = {
    "ts": "TypeScript", "tsx": "TypeScript",
    "js": "JavaScript", "jsx": "JavaScript", "mjs": "JavaScript", "cjs": "JavaScript",
    "py": "Python", "rb": "Ruby", "go": "Go", "java": "Java", "cs": "C#",
    "cpp": "C++", "cc": "C++", "c": "C", "h": "C", "rs": "Rust", "kt": "Kotlin",
    "swift": "Swift", "php": "PHP", "sh": "Shell", "bash": "Shell",
    "ps1": "PowerShell", "psm1": "PowerShell", "psd1": "PowerShell",
    "tf": "HCL", "hcl": "HCL", "json": "JSON", "yaml": "YAML", "yml": "YAML",
    "md": "Markdown", "html": "HTML", "css": "CSS", "scss": "SCSS", "sql": "SQL",
}

_TEST_FILE = re.compile(r"(\.(test|spec)\.[^.]+$|_test\.[^.]+$|Test\.[^.]+$)")
_TEST_PATH = re.compile(r"(^|/)(test|tests|__tests__|spec|specs)/")


def write_json(path: Path, value: Any) -> None:
    """Write ``value`` as pretty-printed JSON to ``path``.

    Creates parent directories as needed. Always emits a trailing newline so
    artifact files diff cleanly.
    """
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(value, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")


def read_json(path: Path) -> Any:
    """Read and parse a JSON file. Empty files are treated as ``None``."""
    text = path.read_text(encoding="utf-8").strip()
    if not text:
        return None
    return json.loads(text)


def changed_files(files: list[str]) -> list[dict[str, Any]]:
    """Classify changed files with a best-effort language and ``isTest`` flag."""
    out: list[dict[str, Any]] = []
    for f in files:
        ext = f.rsplit(".", 1)[-1].lower() if "." in f else ""
        out.append(
            {
                "file": f,
                "language": _EXT_LANG.get(ext, "Other"),
                "isTest": bool(_TEST_FILE.search(f) or _TEST_PATH.search(f)),
            }
        )
    return out


__all__ = ["changed_files", "read_json", "write_json"]
