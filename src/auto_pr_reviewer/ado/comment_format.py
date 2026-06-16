"""Pluggable PR-comment formatter.

The original ``comment_body()`` in :mod:`auto_pr_reviewer.ado.legacy`
hardcoded the Markdown layout of every posted finding. This module
extracts that layout into a small abstraction so users can override it
with a custom Jinja2 template without touching code.

Two formatters ship out of the box:

* :class:`DefaultCommentFormatter` — the original layout, kept as the
  back-compat default. Selected when neither ``COMMENT_TEMPLATE_PATH``
  nor ``COMMENT_TEMPLATE`` is set in the environment.
* :class:`TemplateCommentFormatter` — renders a user-provided Jinja2
  template (loaded from a file path). Selected when
  ``COMMENT_TEMPLATE_PATH`` points at an existing file.

The dedupe marker (``<!-- prb:<key> -->``) is always the last line of
the rendered body, on its own line, regardless of template content.
This is what :func:`auto_pr_reviewer.ado.posting.existing_bot_markers`
scans for — see ``_MARKER_RE`` in that module. Templates that inline
``{{ marker }}`` for display are fine; the canonical dedupe line is
appended automatically so the regex keeps finding it.
"""
from __future__ import annotations

import os
import re
from dataclasses import dataclass
from pathlib import Path
from typing import Any, Mapping, Protocol, runtime_checkable

from ..config import ConfigError


# --- Shared constants ------------------------------------------------------

#: Severity → emoji + label used in both the default formatter and the
#: ``severity_label`` placeholder exposed to user templates.
#:
#: The emoji for ``nit`` is a lightbulb (💡) rather than a white
#: circle (⚪) because at PR-comment scale the lightbulb reads as
#: "informational nit" and the white circle reads as "missing /
#: disabled". The shipped :file:`comment.md.example` uses these
#: labels verbatim via the ``{{ severity_label }}`` placeholder.
SEVERITY_LABEL: dict[str, str] = {
    "blocker": "🔴 blocker",
    "major":   "🟠 major",
    "minor":   "🟡 minor",
    "nit":     "💡 nit",
}

#: The dedupe-marker prefix. Kept in sync with
#: :data:`auto_pr_reviewer.ado.posting.MARKER_PREFIX`.
_MARKER_PREFIX = "prb"

#: Regex matching a marker line on its own. Used to strip any
#: ``{{ marker }}`` block the user inlined so we can re-append the
#: canonical form exactly once at the end of the body.
_MARKER_LINE_RE = re.compile(
    rf"(?m)^<!--\s*{_MARKER_PREFIX}:([a-zA-Z0-9]{{6,32}})\s*-->\s*$"
)

#: Env var names recognised by :func:`build_formatter`.
ENV_TEMPLATE_PATH = "COMMENT_TEMPLATE_PATH"


# --- Helpers (also exported as Jinja2 filters) -----------------------------

def truncate(text: Any, max_chars: int) -> str:
    """Truncate ``text`` to at most ``max_chars`` characters.

    Appends a horizontal ellipsis (``…``) when truncation actually
    happens so the reader sees the body was cut. Returns an empty
    string for non-string inputs.
    """
    s = "" if text is None else str(text)
    if max_chars <= 0 or len(s) <= max_chars:
        return s
    if max_chars == 1:
        return "…"
    return s[: max_chars - 1] + "…"


def join_list(value: Any, sep: str = ", ") -> str:
    """Join a list-ish value as ``str``.

    ``None`` and Jinja2 ``Undefined`` both become ``""``; a scalar is
    returned as-is (no separator). Defensive against the
    ``ChainableUndefined`` we register on the Jinja environment so
    templates can reference optional fields without ``{% if %}``
    guards.
    """
    if value is None:
        return ""
    # jinja2.Undefined is intentionally not a None — handle it via
    # ``str(value)`` falling back to "".
    try:
        is_iter = isinstance(value, (list, tuple, set))
    except Exception:  # pragma: no cover - defensive
        is_iter = False
    if is_iter:
        return sep.join(str(v) for v in value)
    s = str(value)
    if s.startswith("<undefined") or s == "":
        return ""
    return s


def fence(value: Any, language: str = "") -> str:
    """Wrap ``value`` in a Markdown fenced code block.

    Returns an empty string for falsy input or Jinja2 ``Undefined`` so
    ``{{ suggestion | fence }}`` produces nothing when there is no
    suggestion. ``language`` is appended after the opening fence
    (e.g. ``fence(s, "ts")`` → `` ```ts ``).

    The fence width adapts to the input: if the body already contains
    a run of ``N`` backticks, the fence uses ``N+1`` backticks so the
    block still terminates cleanly. This matches the smart-fence logic
    in the original ``comment_body``.
    """
    if value is None:
        return ""
    # jinja2.Undefined
    try:
        body = str(value)
    except Exception:  # pragma: no cover - defensive
        return ""
    if not body or body.startswith("<undefined"):
        return ""
    body = body.rstrip()
    if not body:
        return ""

    longest = current = 0
    for ch in body:
        if ch == "`":
            current += 1
            if current > longest:
                longest = current
        else:
            current = 0
    ticks = "`" * max(3, longest + 1)
    lang = language.strip()
    opener = f"{ticks}{lang}" if lang else ticks
    return f"{opener}\n{body}\n{ticks}"


def marker_line(key: str) -> str:
    """Return the canonical dedupe-marker line for ``key``."""
    return f"<!-- {_MARKER_PREFIX}:{key} -->"


# --- Formatter protocol ----------------------------------------------------

@runtime_checkable
class CommentFormatter(Protocol):
    """Anything that can render a single finding as a Markdown body."""

    def format(
        self,
        finding: Mapping[str, Any],
        *,
        key: str,
        max_chars: int = 20000,
        summary: str | None = None,
    ) -> str:
        """Render ``finding`` as the comment body for the dedupe key ``key``."""
        ...


# --- Default formatter (back-compat) ---------------------------------------

@dataclass(frozen=True)
class DefaultCommentFormatter:
    """The original hardcoded formatter.

    This is what every existing run produces. Kept as the default so
    that callers who never set ``COMMENT_TEMPLATE_PATH`` see no
    change in posted output.
    """

    def format(
        self,
        finding: Mapping[str, Any],
        *,
        key: str,
        max_chars: int = 20000,
        summary: str | None = None,  # noqa: ARG002 - accepted for API parity
    ) -> str:
        severity = str(finding.get("severity", ""))
        title = finding.get("title", "")
        confidence = finding.get("confidence")
        context_basis = finding.get("context_basis") or finding.get("contextBasis")
        evidence = finding.get("evidence") or {}
    
        # ── Header ────────────────────────────────────────────────────────────
        parts: list[str] = [
            f"#### {SEVERITY_LABEL.get(severity, severity)} — {title}",
        ]
    
        # ── Meta subtitle (confidence + context basis) ─────────────────────
        meta_parts: list[str] = []
        if confidence:
            meta_parts.append(self._confidence_label(confidence))
        if context_basis:
            meta_parts.append(f"basis: {self._context_basis_label(context_basis)}")
        if meta_parts:
            parts.append(f"<sub>{' · '.join(meta_parts)}</sub>")
    
        parts.append("")
    
        # ── Message ────────────────────────────────────────────────────────
        parts.append(truncate(finding.get("message", ""), 5000))
    
        # ── Evidence block ────────────────────────────────────────────────
        evidence_parts: list[str] = []
        if evidence.get("whyNewInThisPr"):
            evidence_parts.append(
                f"> **Why introduced by this PR:** {evidence['whyNewInThisPr']}"
            )
        if evidence.get("whyNotIntentional"):
            evidence_parts.append(
                f"> **Why unlikely to be intentional:** {evidence['whyNotIntentional']}"
            )
        if evidence.get("changedLines"):
            lines = ", ".join(str(ln) for ln in evidence["changedLines"][:20])
            evidence_parts.append(f"> **Affected lines:** {lines}")
        if evidence.get("contextFilesRead"):
            files = join_list(list(evidence["contextFilesRead"])[:10])
            evidence_parts.append(f"> **Context read:** {files}")
    
        if evidence_parts:
            parts.extend(["", *evidence_parts])
    
        # ── Suggested fix ─────────────────────────────────────────────────
        if finding.get("suggestion"):
            parts.extend([
                "",
                "**Suggested fix:**",
                "",
                fence(finding["suggestion"]),
            ])
    
        # ── Assemble, truncate, stamp ─────────────────────────────────────
        body = "\n".join(parts)
        body = truncate(body, max_chars - 64)
        body += f"\n\n{marker_line(key)}"
        return body
    
    
    # ── Private label helpers (mirror the Jinja2 macros exactly) ──────────────
    
    def _confidence_label(self, c: str) -> str:
        return {
            "high":   "confidence: high ✓",
            "medium": "confidence: medium ⚠",
            "low":    "confidence: low — verify before acting",
        }.get(c, f"confidence: {c}")
    
    
    def _context_basis_label(self, cb: str) -> str:
        return {
            "diff-only":               "diff only",
            "surrounding-code-read":   "diff + surrounding code",
            "full-module-review":      "full module reviewed",
        }.get(cb, cb)


# --- Jinja2 template formatter --------------------------------------------

@dataclass(frozen=True)
class TemplateCommentFormatter:
    """Render a user-provided Jinja2 template.

    The template is plain Markdown with ``{{ placeholder }}`` tokens.
    All placeholders are resolved against a flat context (no need to
    know the Python object graph), plus ``evidence`` for nested
    fields. Available placeholders:

    ===================  ===========================================
    Token                Source
    ===================  ===========================================
    ``title``            ``finding.title``
    ``message``          ``finding.message``
    ``severity``         ``finding.severity`` (``"major"`` etc.)
    ``severity_label``   ``"🟠 major"`` etc. (see :data:`SEVERITY_LABEL`)
    ``confidence``       ``finding.confidence`` (``""`` when unset)
    ``context_basis``    ``finding.contextBasis``
    ``suggestion``       ``finding.suggestion``
    ``file``             ``finding.file``
    ``line``             ``finding.line``
    ``key``              raw dedupe key
    ``marker``           ``"prb:<key>"`` (for in-body display)
    ``summary``          PR-level summary (passed by the caller)
    ``evidence``         dict with the four sub-fields below
    ``evidence.whyNewInThisPr``
    ``evidence.whyNotIntentional``
    ``evidence.contextFilesRead``  (joined via ``| join_list``)
    ``evidence.changedLines``       (joined via ``| join_list``)
    ===================  ===========================================

    Three custom filters are exposed: ``join_list`` (default sep
    ``", "``), ``fence`` (default no language), ``fence_lang`` (explicit
    language argument). ``autoescape`` is **off** because the body is
    Markdown, not HTML.
    """

    template_text: str

    @classmethod
    def from_path(cls, path: Path) -> "TemplateCommentFormatter":
        if not path.exists():
            raise ConfigError(
                f"{ENV_TEMPLATE_PATH} points to {str(path)!r}, but the file does not exist."
            )
        return cls(template_text=path.read_text(encoding="utf-8"))

    @classmethod
    def from_env(cls) -> "TemplateCommentFormatter | None":
        """Return a template formatter from env, or ``None`` if unset / file missing."""
        raw = os.getenv(ENV_TEMPLATE_PATH)
        if not raw:
            return None
        path = Path(raw)
        if not path.exists():
            raise ConfigError(
                f"{ENV_TEMPLATE_PATH} points to {str(path)!r}, but the file does not exist."
            )
        return cls(template_text=path.read_text(encoding="utf-8"))

    # Kept around so callers can introspect / re-derive the env.
    def __post_init__(self) -> None:  # pragma: no cover - trivial
        if not self.template_text or not self.template_text.strip():
            raise ConfigError(
                f"{ENV_TEMPLATE_PATH} resolved to an empty template."
            )

    def _build_context(self, finding: Mapping[str, Any], key: str, summary: str | None) -> dict[str, Any]:
        severity = str(finding.get("severity") or "")
        return {
            "title": str(finding.get("title") or ""),
            "message": str(finding.get("message") or ""),
            "severity": severity,
            "severity_label": SEVERITY_LABEL.get(severity, severity),
            "confidence": str(finding.get("confidence") or ""),
            "context_basis": str(finding.get("contextBasis") or ""),
            "suggestion": str(finding.get("suggestion") or ""),
            "file": str(finding.get("file") or ""),
            "line": finding.get("line"),
            "key": key,
            "marker": f"{_MARKER_PREFIX}:{key}",
            "summary": str(summary or ""),
            "evidence": dict(finding.get("evidence") or {}),
        }

    def format(
        self,
        finding: Mapping[str, Any],
        *,
        key: str,
        max_chars: int = 20000,
        summary: str | None = None,
    ) -> str:
        # Import lazily so the default-formatter code path does not
        # pay the jinja2 import cost (and so projects that never set
        # COMMENT_TEMPLATE_PATH can technically drop jinja2 — though we
        # still list it as a dep for parity).
        from jinja2 import ChainableUndefined, Environment

        env = Environment(
            autoescape=False,
            trim_blocks=True,
            lstrip_blocks=True,
            undefined=ChainableUndefined,
        )
        env.filters["join_list"] = join_list
        env.filters["fence"] = fence
        env.filters["fence_lang"] = fence

        template = env.from_string(self.template_text)
        rendered = template.render(**self._build_context(finding, key, summary))

        # If the template inlined `{{ marker }}` (or hand-wrote a
        # marker line), strip it — we always want exactly one marker
        # line, on its own, at the end of the body.
        rendered = _MARKER_LINE_RE.sub("", rendered).rstrip()
        rendered = truncate(rendered, max_chars - 64)
        rendered += f"\n\n{marker_line(key)}"
        return rendered


# --- Factory ---------------------------------------------------------------

def build_formatter() -> CommentFormatter:
    """Pick the right formatter from the environment.

    * ``COMMENT_TEMPLATE_PATH`` set + file exists → :class:`TemplateCommentFormatter`
    * otherwise → :class:`DefaultCommentFormatter`

    A missing file is a hard error: silently falling back to the
    default would make a typo in the env var look like "comments are
    formatted normally" when in fact the user expected a different
    layout.
    """
    return TemplateCommentFormatter.from_env() or DefaultCommentFormatter()


__all__ = [
    "CommentFormatter",
    "DefaultCommentFormatter",
    "ENV_TEMPLATE_PATH",
    "SEVERITY_LABEL",
    "TemplateCommentFormatter",
    "build_formatter",
    "fence",
    "join_list",
    "marker_line",
    "truncate",
]
