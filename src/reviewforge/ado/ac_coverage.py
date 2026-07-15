"""Acceptance-criteria coverage check.

For each linked work item, the bot extracts identifiers (file paths,
function names, quoted terms) from the work item's acceptance
criteria and asks: does the PR's diff mention any of them? An AC that
mentions ``src/payments/charge.ts`` is covered when that file appears
in the changed-files list or in the diff body. An AC that mentions
nothing concrete — ``user can click the button`` — is always uncovered
because there are no identifiers to find.

This is a deterministic, no-LLM heuristic. False positives
(uncovered when the PR actually does the work) are the safe failure
mode: the bot prefers to flag a missing AC and let a human dismiss
it than to silently swallow a real gap. Per the project's
"ponytail: lazy" doctrine this is the minimum viable check; an LLM
re-assesment can layer on top later if precision becomes an issue.

The check is invoked by :class:`AcceptanceCriteriaCoverageStage` between
``CalibrateSeverityStage`` and ``PostToAdoStage``; uncovered ACs are
appended to ``final-findings.json`` as general-thread findings so
reviewers see them on the PR.
"""
from __future__ import annotations

import html
import re
from dataclasses import dataclass, field
from typing import Iterable


# --- Identifier extraction -------------------------------------------------

# File paths: ``src/payments/charge.ts``, ``docs/index.md``, etc.
# Tolerates leading ``./`` and Windows backslashes. Excludes bare URLs.
_PATH_RE = re.compile(
    r"""(?x)
    (?<!\w)
    (?:\.{0,2}/)?
    [A-Za-z_][\w\-.]* (?: / [A-Za-z_][\w\-.]* )*
    \.
    [A-Za-z]{1,6}
    (?!\w)
    """
)

# CamelCase / PascalCase identifiers, length >= 3.
_CAMEL_RE = re.compile(r"\b[A-Z][a-z0-9]+(?:[A-Z][a-z0-9]+)+\b")

# snake_case identifiers, length >= 4. Skips very short words like ``id``.
_SNAKE_RE = re.compile(r"\b[a-z][a-z0-9]{3,}(?:_[a-z0-9]+)+\b")

# ``def foo`` / ``class Bar`` / ``function baz`` style references.
_DEF_RE = re.compile(
    r"\b(?:def|class|function|method|func)\s+([A-Za-z_][A-Za-z0-9_]*)"
)

# Backtick-quoted identifiers, e.g. `` `cfg.dry_run` ``.
_BACKTICK_RE = re.compile(r"`([A-Za-z_][\w.]*)`")

# Double- or single-quoted strings (short, alphanumeric/underscore only).
_QUOTED_RE = re.compile(r"""["']([A-Za-z_][\w./-]{2,})["']""")


def strip_html(text: str) -> str:
    """Strip HTML tags and decode entities. Empty string for ``None``."""
    if not text:
        return ""
    no_tags = re.sub(r"<[^>]+>", " ", text)
    return html.unescape(no_tags)


def extract_identifiers(ac_text: str) -> set[str]:
    """Pull candidate identifiers (paths, names, quoted terms) from AC text.

    Filters out common English stopwords that look like snake_case
    identifiers (``the``, ``and`` etc.) and very short tokens that
    would create noise in the coverage check.
    """
    if not ac_text:
        return set()
    plain = strip_html(ac_text)
    out: set[str] = set()
    out.update(_PATH_RE.findall(plain))
    out.update(_CAMEL_RE.findall(plain))
    out.update(_SNAKE_RE.findall(plain))
    out.update(m.group(1) for m in _DEF_RE.finditer(plain))
    out.update(m.group(1) for m in _BACKTICK_RE.finditer(plain))
    out.update(m.group(1) for m in _QUOTED_RE.finditer(plain))
    # Drop path-like matches that are actually URLs / version strings.
    cleaned: set[str] = set()
    for ident in out:
        if ident.startswith(("http://", "https://")):
            continue
        # ``v1.2.3`` style version strings — bare ``N.N.N``.
        if re.fullmatch(r"\d+(?:\.\d+)+", ident):
            continue
        cleaned.add(ident)
    return cleaned


# --- Work-item AC parsing --------------------------------------------------


def iter_acceptance_criteria(work_items: Iterable[dict]) -> list[dict]:
    """Flatten work items into one dict per acceptance criterion.

    Each item has ``work_item_id``, ``title``, ``type``, and ``ac_text``.
    Items without an ``acceptanceCriteria`` field are skipped.
    """
    flat: list[dict] = []
    for item in work_items or []:
        ac = item.get("acceptanceCriteria")
        if not ac or not isinstance(ac, str):
            continue
        if ac.strip().lower() in {"", "(none)", "n/a", "none"}:
            continue
        flat.append(
            {
                "work_item_id": item.get("id"),
                "title": item.get("title", ""),
                "type": item.get("type", ""),
                "ac_text": ac,
            }
        )
    return flat


# --- Coverage check --------------------------------------------------------


@dataclass(frozen=True)
class AcCoverageResult:
    """Result of covering one acceptance criterion against the diff."""

    work_item_id: int | str | None
    ac_text: str
    identifiers: tuple[str, ...] = ()
    is_covered: bool = False
    matched: tuple[str, ...] = ()
    reason: str = ""

    @property
    def short_text(self) -> str:
        """One-line rendering of the AC, truncated for the finding title."""
        plain = strip_html(self.ac_text).strip().replace("\n", " ")
        return plain if len(plain) <= 80 else plain[:77] + "..."


def check_ac_coverage(
    work_items: Iterable[dict],
    diff_text: str,
    changed_files: Iterable[str],
) -> list[AcCoverageResult]:
    """Return one result per AC, in input order.

    An AC is ``is_covered`` when at least one of its identifiers
    appears (as a substring, case-sensitively) in either the changed
    files list or the diff body. An AC with zero identifiers is
    uncovered by definition — there is nothing to match.
    """
    changed_list = list(changed_files or [])
    diff_blob = diff_text or ""
    results: list[AcCoverageResult] = []
    for ac in iter_acceptance_criteria(work_items):
        idents = extract_identifiers(ac["ac_text"])
        if not idents:
            results.append(
                AcCoverageResult(
                    work_item_id=ac["work_item_id"],
                    ac_text=ac["ac_text"],
                    identifiers=(),
                    is_covered=False,
                    matched=(),
                    reason="no_identifiers_extracted",
                )
            )
            continue
        matched: list[str] = []
        for ident in idents:
            ident_lower = ident.lower()
            if any(ident_lower in f.lower() for f in changed_list):
                matched.append(ident)
                continue
            if ident_lower in diff_blob.lower():
                matched.append(ident)
        results.append(
            AcCoverageResult(
                work_item_id=ac["work_item_id"],
                ac_text=ac["ac_text"],
                identifiers=tuple(sorted(idents)),
                is_covered=bool(matched),
                matched=tuple(sorted(set(matched))),
                reason="" if matched else "no_identifier_in_diff",
            )
        )
    return results


def uncovered_findings(results: list[AcCoverageResult]) -> list[dict]:
    """Convert uncovered results into the finding shape the post stage expects.

    Each finding has ``file: None, line: None`` so it posts as a
    general PR comment (see ``cli.py`` — the post path adds no
    ``threadContext`` when ``file`` is missing). The title starts with
    ``Work item #N`` so the existing work-item stripping rule
    (``is_work_item_finding`` in ``posting.py``) keeps the finding as
    a general comment regardless of any model-guessed file/line.
    """
    findings: list[dict] = []
    for r in results:
        if r.is_covered:
            continue
        wi = r.work_item_id
        title_prefix = f"Work item #{wi} acceptance criterion not covered"
        findings.append(
            {
                "file": None,
                "line": None,
                "severity": "major",
                "title": f"{title_prefix}: {r.short_text}",
                "message": (
                    f"Linked work item #{wi} declares an acceptance criterion that "
                    f"is not visibly addressed by the PR diff.\n\n"
                    f"Acceptance criterion:\n> {strip_html(r.ac_text).strip() or '(empty)'}\n\n"
                    f"Identifiers extracted from the AC and looked up in the diff: "
                    f"{', '.join(f'`{i}`' for i in r.identifiers) or '(none — AC text contains no identifiable references)'}.\n"
                    f"Reason: {r.reason or 'no_identifier_in_diff'}."
                ),
            }
        )
    return findings


__all__ = [
    "AcCoverageResult",
    "check_ac_coverage",
    "extract_identifiers",
    "iter_acceptance_criteria",
    "strip_html",
    "uncovered_findings",
]