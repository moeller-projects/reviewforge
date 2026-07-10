"""Enforce the hard rules from ``AGENTS.md`` as automated tests.

Each numbered rule has a corresponding test (or class) below. The rule
text is quoted in the docstring so the link between AGENTS.md and the
test suite is explicit. If you change AGENTS.md's hard rules, update
the test docstring and assertion in the same commit.

References:

* ``AGENTS.md`` §4 — hard rules (acceptance gates, not style).
* ``AGENTS.md`` §6 — idempotent posting contract.
* ``docs/reference/ado-integration.md`` — dedupe-key + marker format.
"""
from __future__ import annotations

import importlib
import re
from pathlib import Path

import pytest

ROOT = Path(__file__).resolve().parent.parent


# ---------------------------------------------------------------------------
# Rule 1 — ``scripts/*.py`` must remain thin shims.
#
# Quoted from AGENTS.md §4.1:
#   ``scripts/*.py`` must remain thin shims. No business logic in
#   scripts/main.py or scripts/ado_review.py. New behavior goes in
#   src/auto_pr_reviewer/.
# ---------------------------------------------------------------------------

ALLOWED_SHIM_FUNCTIONS: dict[str, set[str]] = {
    "main.py": {"_ensure_src_on_path", "main"},
    "ado_review.py": {"_ensure_src_on_path", "main"},
    "review.py": {"main"},
}

# Imports that would indicate business logic leaked into a shim.
BUSINESS_IMPORTS: tuple[str, ...] = (
    "from auto_pr_reviewer.pipeline",
    "from auto_pr_reviewer.config",
    "from auto_pr_reviewer.ado.client",
    "from auto_pr_reviewer.ado.posting",
    "from auto_pr_reviewer.ado.diff_mapper",
    "from auto_pr_reviewer.ai.runner",
    "from auto_pr_reviewer.artifacts",
    "from auto_pr_reviewer.git",
)


class TestScriptsRemainThinShims:
    """AGENTS.md §4.1: scripts/*.py are thin shims."""

    @pytest.mark.parametrize("script_name", ["main.py", "ado_review.py", "review.py"])
    def test_only_allowed_functions_defined(self, script_name: str) -> None:
        text = (ROOT / "scripts" / script_name).read_text(encoding="utf-8")
        defined = set(re.findall(r"^def\s+([A-Za-z_][A-Za-z0-9_]*)\s*\(", text, flags=re.MULTILINE))
        allowed = ALLOWED_SHIM_FUNCTIONS[script_name]
        extras = defined - allowed
        assert not extras, (
            f"scripts/{script_name} defines functions outside the allow-list {allowed}: {extras}"
        )

    @pytest.mark.parametrize("script_name", ["main.py", "ado_review.py"])
    def test_no_business_imports(self, script_name: str) -> None:
        text = (ROOT / "scripts" / script_name).read_text(encoding="utf-8")
        leaks = [imp for imp in BUSINESS_IMPORTS if imp in text]
        assert not leaks, (
            f"scripts/{script_name} imports business modules — those belong in src/auto_pr_reviewer/: {leaks}"
        )

    @pytest.mark.parametrize("script_name", ["main.py", "ado_review.py"])
    def test_under_line_cap(self, script_name: str) -> None:
        # The shims are intentionally tiny. 60 lines is a soft ceiling;
        # the review.py compat shim is even tighter.
        text = (ROOT / "scripts" / script_name).read_text(encoding="utf-8")
        cap = 60 if script_name in {"main.py", "ado_review.py"} else 15
        assert text.count("\n") < cap, (
            f"scripts/{script_name} is over the {cap}-line shim cap — extract logic into the package."
        )


# ---------------------------------------------------------------------------
# Rule 3 — Posting is idempotent. Rule 5 — marker regex anchored.
#
# Quoted from AGENTS.md §4.3 / §4.5:
#   Posting is idempotent. Every comment gets a prb:<key> marker.
#   Marker regex is anchored to a whole line.
#   ^prb:([a-zA-Z0-9]{6,32})$ in src/auto_pr_reviewer/ado/posting.py.
# ---------------------------------------------------------------------------


class TestMarkerContract:
    """AGENTS.md §4.3 + §4.5: marker prefix, regex shape, dedupe-key shape."""

    def test_marker_prefix_is_prb(self) -> None:
        from auto_pr_reviewer.ado import posting
        assert posting.MARKER_PREFIX == "prb", (
            "MARKER_PREFIX changed — this is a breaking change to the idempotency contract."
        )

    def test_marker_regex_anchored_whole_line(self) -> None:
        from auto_pr_reviewer.ado import posting
        pattern = posting._MARKER_RE.pattern  # noqa: SLF001 — internal contract
        assert pattern.startswith("(?m)^"), "Marker regex must be multiline + line-anchored."
        assert "prb:" in pattern, "Marker regex must reference the prb: prefix."
        assert pattern.rstrip().endswith("$"), "Marker regex must anchor to end of line."

    def test_marker_regex_accepts_only_legal_chars(self) -> None:
        from auto_pr_reviewer.ado import posting
        m = posting._MARKER_RE.search("prb:a1b2c3d4e5f6")  # noqa: SLF001
        assert m and m.group(1) == "a1b2c3d4e5f6"
        # Too short → reject.
        assert posting._MARKER_RE.search("prb:abc") is None  # noqa: SLF001
        # Bad char → reject.
        assert posting._MARKER_RE.search("prb:a1!2c3d4e5f6") is None  # noqa: SLF001

    def test_dedupe_key_is_12_hex(self) -> None:
        from auto_pr_reviewer.ado.posting import dedupe_key
        k = dedupe_key({"file": "x.py", "line": 1, "severity": "nit", "title": "T", "message": "M"})
        assert len(k) == 12
        assert all(c in "0123456789abcdef" for c in k)


# ---------------------------------------------------------------------------
# Rule 4 — ``ARTIFACT_NAMES`` is the stable contract; append-only.
#
# Quoted from AGENTS.md §4.4:
#   Do not edit ARTIFACT_NAMES lightly. It is the stable contract.
#   Add new files at the end; never rename or remove an entry.
# ---------------------------------------------------------------------------


# Snapshot of the canonical names in the order declared in
# ``artifacts/manager.py``. Any drift here is a breaking change.
CANONICAL_ARTIFACT_NAMES: tuple[str, ...] = (
    "metadata.json",
    "diff.patch",
    "changed-files.json",
    "commits.txt",
    "intent.json",
    "context-plan.json",
    "collected-context.json",
    "context-digest.json",
    "candidate-findings.json",
    "verified-findings.json",
    "severity-findings.json",
    "final-findings.json",
    "posted-comments.json",
    "run-summary.json",
    "review-system.combined.md",
    "work-items.json",
    "threads.json",
)


class TestArtifactContract:
    """AGENTS.md §4.4: ARTIFACT_NAMES is append-only."""

    def test_artifact_names_match_canonical_snapshot(self) -> None:
        from auto_pr_reviewer.artifacts.manager import ARTIFACT_NAMES
        # New entries may be appended at the end. No entry may be
        # reordered, renamed, or removed.
        assert ARTIFACT_NAMES[: len(CANONICAL_ARTIFACT_NAMES)] == CANONICAL_ARTIFACT_NAMES, (
            "ARTIFACT_NAMES drifted from the canonical snapshot — this is a breaking change. "
            "Add new entries only at the end and update CANONICAL_ARTIFACT_NAMES in this test."
        )

    def test_artifact_names_no_duplicates(self) -> None:
        from auto_pr_reviewer.artifacts.manager import ARTIFACT_NAMES
        assert len(set(ARTIFACT_NAMES)) == len(ARTIFACT_NAMES)


# ---------------------------------------------------------------------------
# Rule 7 — ``open-prs`` from the Python CLI is intentionally unsupported.
#
# Quoted from AGENTS.md §4.7:
#   open-prs from the Python CLI is intentionally unsupported. It fails
#   fast with a pointer to ./run-open-prs.ps1. The architecture runs
#   one container per PR — do not change this without a spec.
# ---------------------------------------------------------------------------


class TestOpenPrsCliIsUnsupported:
    """AGENTS.md §4.7: cmd_open_prs is a no-op pointer to PowerShell."""

    def test_open_prs_subcommand_present(self) -> None:
        from auto_pr_reviewer.cli import build_parser
        # The subcommand must exist so users get a clear error.
        parser = build_parser()
        sub_actions = [
            a for a in parser._actions if hasattr(a, "choices") and a.choices  # noqa: SLF001
        ]
        sub_names = set()
        for a in sub_actions:
            sub_names.update(a.choices.keys())
        assert "open-prs" in sub_names

    def test_cmd_open_prs_fails_fast(self, capsys) -> None:
        # The function must refuse with a non-zero rc and point the user
        # at the PowerShell wrapper, regardless of input. Returns int,
        # does not raise SystemExit.
        from auto_pr_reviewer.cli import cmd_open_prs
        args = type(
            "Args",
            (),
            {
                "ado_org": "x",
                "ado_project": "P",
                "ado_token": "tok",
                "target_branches": "",
                "max": 0,
            },
        )()
        rc = cmd_open_prs(args)
        assert rc != 0, "cmd_open_prs must return non-zero — it's a no-op pointer to PowerShell."
        err = capsys.readouterr().err
        assert "run-open-prs.ps1" in err or "PowerShell" in err or "run-open-prs" in err