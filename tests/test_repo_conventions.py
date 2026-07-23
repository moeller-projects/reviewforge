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

import subprocess
from pathlib import Path

import pytest

ROOT = Path(__file__).resolve().parent.parent


# ---------------------------------------------------------------------------
# Repository hygiene — no malformed tracked path names.
#
# An agent once committed an empty file whose name was a pasted shell command.
# Tracked file and directory names must never contain shell-command
# fragments; this class of mistake fails CI.
# ---------------------------------------------------------------------------


class TestTrackedPathNames:
    """No tracked path contains ';', ':', or a 'FILE:'-style prefix."""

    def test_no_tracked_name_has_shell_command_fragments(self) -> None:
        out = subprocess.run(
            ["git", "ls-files", "-z"],
            check=True,
            capture_output=True,
            cwd=ROOT,
        ).stdout.decode("utf-8")
        names = [n for n in out.split("\0") if n]
        assert names, "git ls-files returned nothing — is this a checkout?"
        for name in names:
            parts = name.split("/")
            for part in parts:
                assert ";" not in part, f"malformed tracked name: {name!r}"
                assert ":" not in part, f"malformed tracked name: {name!r}"
                assert not part.startswith("FILE:"), f"malformed tracked name: {name!r}"



# ---------------------------------------------------------------------------
# Rule 3 — Posting is idempotent. Rule 5 — marker regex anchored.
#
# Quoted from AGENTS.md §4.3 / §4.5:
#   Posting is idempotent. Every comment gets a prb:<key> marker.
#   Marker regex is anchored to a whole line.
#   ^prb:([a-zA-Z0-9]{6,32})$ in src/reviewforge/ado/posting.py.
# ---------------------------------------------------------------------------


class TestMarkerContract:
    """AGENTS.md §4.3 + §4.5: marker prefix, regex shape, dedupe-key shape."""

    def test_marker_prefix_is_prb(self) -> None:
        from reviewforge.ado import posting
        assert posting.MARKER_PREFIX == "prb", (
            "MARKER_PREFIX changed — this is a breaking change to the idempotency contract."
        )

    def test_marker_regex_anchored_whole_line(self) -> None:
        from reviewforge.ado import posting
        pattern = posting._MARKER_RE.pattern  # noqa: SLF001 — internal contract
        assert pattern.startswith("(?m)^"), "Marker regex must be multiline + line-anchored."
        assert "prb:" in pattern, "Marker regex must reference the prb: prefix."
        assert pattern.rstrip().endswith("$"), "Marker regex must anchor to end of line."

    def test_marker_regex_accepts_only_legal_chars(self) -> None:
        from reviewforge.ado import posting
        m = posting._MARKER_RE.search("prb:a1b2c3d4e5f6")  # noqa: SLF001
        assert m and m.group(1) == "a1b2c3d4e5f6"
        # Too short → reject.
        assert posting._MARKER_RE.search("prb:abc") is None  # noqa: SLF001
        # Bad char → reject.
        assert posting._MARKER_RE.search("prb:a1!2c3d4e5f6") is None  # noqa: SLF001

    def test_dedupe_key_is_12_hex(self) -> None:
        from reviewforge.ado.posting import dedupe_key
        k = dedupe_key({"file": "x.py", "line": 1, "severity": "nit", "title": "T", "message": "M"})
        assert len(k) == 12
        assert all(c in "0123456789abcdef" for c in k)


# ---------------------------------------------------------------------------
# Rule 4 — ``ARTIFACT_NAMES`` is the stable contract; append-only.
#
# Quoted from AGENTS.md §4.4:
#   Do not edit ARTIFACT_NAMES lightly. It is the stable contract.
# Snapshot of the canonical names after the explicit 0.3 artifact migration.
CANONICAL_ARTIFACT_NAMES: tuple[str, ...] = (
    "metadata.json",
    "diff.patch",
    "changed-files.json",
    "commits.txt",
    "final-findings.json",
    "posted-comments.json",
    "run-summary.json",
    "review-system.combined.md",
    "work-items.json",
    "threads.json",
    "review-result.json",
)


class TestArtifactContract:
    """AGENTS.md §4.4: ARTIFACT_NAMES is append-only."""

    def test_artifact_names_match_canonical_snapshot(self) -> None:
        from reviewforge.artifacts.manager import ARTIFACT_NAMES
        # New entries may be appended at the end. No entry may be
        # reordered, renamed, or removed.
        assert ARTIFACT_NAMES[: len(CANONICAL_ARTIFACT_NAMES)] == CANONICAL_ARTIFACT_NAMES, (
            "ARTIFACT_NAMES drifted from the canonical snapshot — this is a breaking change. "
            "Add new entries only at the end and update CANONICAL_ARTIFACT_NAMES in this test."
        )

    def test_artifact_names_no_duplicates(self) -> None:
        from reviewforge.artifacts.manager import ARTIFACT_NAMES
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
        from reviewforge.cli import build_parser
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
        from reviewforge.cli import cmd_open_prs
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