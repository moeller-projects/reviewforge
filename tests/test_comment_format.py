"""Tests for the pluggable comment formatter.

These cover:

* :class:`DefaultCommentFormatter` — back-compat output identical to
  the original ``comment_body`` so existing test_ado_review tests keep
  passing.
* :class:`TemplateCommentFormatter` — Jinja2 rendering, placeholder
  context, dot-notation for nested ``evidence`` fields, marker
  invariant (one marker line, last, on its own), truncation,
  missing-file failure mode.
* :func:`build_formatter` — env-driven selection.
"""
from __future__ import annotations

from pathlib import Path
from textwrap import dedent

import pytest

from auto_pr_reviewer.ado import comment_format as cf
from auto_pr_reviewer.ado.comment_format import (
    DefaultCommentFormatter,
    TemplateCommentFormatter,
    build_formatter,
)
from auto_pr_reviewer.config import ConfigError


# --- Fixtures ---------------------------------------------------------------

_BASE_FINDING: dict = {
    "severity": "major",
    "title": "Credential Leak",
    "message": "Token exposed in log.",
    "confidence": "high",
    "suggestion": None,
    "file": "src/logger.ts",
    "line": 42,
    "contextBasis": "extracted from PR diff",
}


@pytest.fixture
def finding():
    return dict(_BASE_FINDING)


@pytest.fixture
def key():
    return "abc123def456"


# --- Default formatter ------------------------------------------------------

class TestDefaultCommentFormatter:
    """The default formatter must match the legacy comment_body output."""

    def test_contains_severity_label(self, finding, key):
        body = DefaultCommentFormatter().format(finding, key=key, max_chars=20000)
        assert "🟠 major" in body

    def test_contains_title(self, finding, key):
        body = DefaultCommentFormatter().format(finding, key=key, max_chars=20000)
        assert "Credential Leak" in body

    def test_contains_marker_last_line(self, finding, key):
        body = DefaultCommentFormatter().format(finding, key=key, max_chars=20000)
        # Marker must be the last line, on its own.
        last_line = body.rstrip().splitlines()[-1]
        assert last_line == f"<!-- prb:{key} -->"

    def test_suggestion_fenced(self, finding, key):
        finding["suggestion"] = "remove the log line"
        body = DefaultCommentFormatter().format(finding, key=key, max_chars=20000)
        assert "```" in body
        assert "remove the log line" in body

    def test_evidence_included(self, finding, key):
        finding["evidence"] = {
            "whyNewInThisPr": "introduced in this commit",
            "whyNotIntentional": "looks accidental",
            "contextFilesRead": ["src/app.ts", "src/logger.ts"],
            "changedLines": [10, 11],
        }
        body = DefaultCommentFormatter().format(finding, key=key, max_chars=20000)
        assert "introduced in this commit" in body
        assert "looks accidental" in body
        assert "src/app.ts" in body
        assert "src/logger.ts" in body

    def test_respects_max_chars(self, finding, key):
        # Marker + padding is 64 chars, so max_chars=100 must clip the body.
        finding["message"] = "x" * 1000
        body = DefaultCommentFormatter().format(finding, key=key, max_chars=100)
        assert len(body) <= 100
        # And the marker must still be the last line.
        assert body.rstrip().splitlines()[-1] == f"<!-- prb:{key} -->"

    def test_missing_severity_falls_through(self, finding, key):
        finding["severity"] = "weird"  # not in SEVERITY_LABEL
        body = DefaultCommentFormatter().format(finding, key=key, max_chars=20000)
        assert "weird" in body  # falls through to raw severity value

    def test_summary_arg_is_accepted_for_api_parity(self, finding, key):
        # The default formatter ignores summary; just make sure the kwarg
        # doesn't crash (so callers can pass it uniformly).
        body = DefaultCommentFormatter().format(
            finding, key=key, max_chars=20000, summary="ignored"
        )
        assert "Credential Leak" in body


# --- Template formatter -----------------------------------------------------

class TestTemplateCommentFormatter:
    def test_basic_placeholders(self, finding, key):
        tmpl = dedent(
            """\
            ## {{ severity_label }}: {{ title }}

            {{ message }}

            _{{ marker }}_
            """
        )
        body = TemplateCommentFormatter(tmpl).format(finding, key=key, max_chars=20000)
        assert "🟠 major" in body
        assert "Credential Leak" in body
        assert "Token exposed in log." in body
        # Marker line appended at the end, regardless of `{{ marker }}` in template.
        assert body.rstrip().splitlines()[-1] == f"<!-- prb:{key} -->"

    def test_summary_placeholder(self, finding, key):
        tmpl = "{{ summary }}\n\n---\n\n{{ title }}"
        body = TemplateCommentFormatter(tmpl).format(
            finding, key=key, max_chars=20000, summary="Doc-level review summary."
        )
        assert "Doc-level review summary." in body
        assert "Credential Leak" in body

    def test_nested_evidence_dot_notation(self, finding, key):
        finding["evidence"] = {
            "whyNewInThisPr": "introduced in this commit",
            "whyNotIntentional": "looks accidental",
            "contextFilesRead": ["src/app.ts", "src/logger.ts"],
            "changedLines": [10, 11],
        }
        tmpl = (
            "**{{ title }}**\n"
            "Why in PR: {{ evidence.whyNewInThisPr }}\n"
            "Files: {{ evidence.contextFilesRead | join_list(', ') }}"
        )
        body = TemplateCommentFormatter(tmpl).format(finding, key=key, max_chars=20000)
        assert "Why in PR: introduced in this commit" in body
        assert "Files: src/app.ts, src/logger.ts" in body

    def test_join_list_filter_handles_none(self, finding, key):
        tmpl = "Files: [{{ evidence.contextFilesRead | join_list('; ') }}]"
        body = TemplateCommentFormatter(tmpl).format(finding, key=key, max_chars=20000)
        # None renders as empty string, no "None" literal.
        assert "Files: []" in body
        assert "None" not in body

    def test_fence_filter(self, finding, key):
        finding["suggestion"] = "logger.remove(token)"
        tmpl = "Suggestion:\n{{ suggestion | fence('ts') }}"
        body = TemplateCommentFormatter(tmpl).format(finding, key=key, max_chars=20000)
        assert "```ts\nlogger.remove(token)\n```" in body

    def test_fence_escapes_inner_backticks(self, finding, key):
        finding["suggestion"] = "```js\nalert(1)\n```"
        tmpl = "Suggestion:\n{{ suggestion | fence }}"
        body = TemplateCommentFormatter(tmpl).format(finding, key=key, max_chars=20000)
        # Outer fence must use 4+ backticks so the inner triple is harmless.
        assert "````\n```js\nalert(1)\n```\n````" in body

    def test_html_marker_line_inlined_in_template_is_normalised(self, finding, key):
        # If the user hand-wrote the canonical HTML-comment-form marker
        # line in the middle of their template, the formatter strips it
        # and re-appends exactly one copy at the end. ``{{ marker }}``
        # is the raw ``prb:<key>`` text for display and is left alone.
        tmpl = (
            "Body: {{ title }}\n"
            f"<!-- prb:{key} -->\n"
        )
        body = TemplateCommentFormatter(tmpl).format(finding, key=key, max_chars=20000)
        # The inlined HTML-comment-form marker line is stripped.
        assert body.count("<!-- prb:") == 1
        # The trailing marker line is exactly the canonical form.
        assert body.rstrip().splitlines()[-1] == f"<!-- prb:{key} -->"

    def test_raw_marker_placeholder_left_for_display(self, finding, key):
        # ``{{ marker }}`` renders as the raw ``prb:<key>`` text and is
        # NOT treated as a dedupe marker. The trailing canonical line
        # is still appended.
        tmpl = "Ref: {{ marker }}\n\nBody: {{ title }}\n"
        body = TemplateCommentFormatter(tmpl).format(finding, key=key, max_chars=20000)
        assert f"Ref: prb:{key}" in body
        # The trailing marker line is exactly the canonical form.
        assert body.rstrip().splitlines()[-1] == f"<!-- prb:{key} -->"

    def test_truncation_keeps_marker_last(self, finding, key):
        finding["message"] = "y" * 5000
        tmpl = "{{ message }}"
        body = TemplateCommentFormatter(tmpl).format(finding, key=key, max_chars=200)
        assert len(body) <= 200
        assert body.rstrip().splitlines()[-1] == f"<!-- prb:{key} -->"

    def test_chainable_undefined_renders_unknown_placeholder_as_empty(self, finding, key):
        # The formatter uses ChainableUndefined (not StrictUndefined) so
        # templates can reference optional fields without ``{% if %}``
        # guards. Typos in placeholder names silently render as empty
        # string — that is the documented behaviour.
        tmpl = "[{{ not_a_field }}]"
        body = TemplateCommentFormatter(tmpl).format(finding, key=key, max_chars=20000)
        assert body.startswith("[]")

    def test_from_path_loads_file(self, finding, key, tmp_path):
        p = tmp_path / "tmpl.md"
        p.write_text("# {{ title }}\n", encoding="utf-8")
        fmt = TemplateCommentFormatter.from_path(p)
        body = fmt.format(finding, key=key, max_chars=20000)
        assert "# Credential Leak" in body

    def test_from_path_missing_file_raises(self, tmp_path):
        with pytest.raises(ConfigError) as exc_info:
            TemplateCommentFormatter.from_path(tmp_path / "nope.md")
        assert "does not exist" in str(exc_info.value)

    def test_empty_template_raises(self):
        with pytest.raises(ConfigError):
            TemplateCommentFormatter("")


# --- build_formatter factory ------------------------------------------------

class TestBuildFormatter:
    def test_default_when_no_env(self, monkeypatch):
        monkeypatch.delenv(cf.ENV_TEMPLATE_PATH, raising=False)
        fmt = build_formatter()
        assert isinstance(fmt, DefaultCommentFormatter)

    def test_template_when_env_set(self, monkeypatch, tmp_path):
        p = tmp_path / "tmpl.md"
        p.write_text("# {{ title }}\n", encoding="utf-8")
        monkeypatch.setenv(cf.ENV_TEMPLATE_PATH, str(p))
        fmt = build_formatter()
        assert isinstance(fmt, TemplateCommentFormatter)

    def test_template_env_missing_file_raises(self, monkeypatch, tmp_path):
        monkeypatch.setenv(cf.ENV_TEMPLATE_PATH, str(tmp_path / "missing.md"))
        with pytest.raises(ConfigError):
            build_formatter()

    def test_template_env_empty_string_treated_as_unset(self, monkeypatch):
        monkeypatch.setenv(cf.ENV_TEMPLATE_PATH, "")
        fmt = build_formatter()
        assert isinstance(fmt, DefaultCommentFormatter)


# --- Helpers ----------------------------------------------------------------

class TestFence:
    def test_plain(self):
        assert cf.fence("print('hi')") == "```\nprint('hi')\n```"

    def test_with_language(self):
        assert cf.fence("let x = 1", "ts") == "```ts\nlet x = 1\n```"

    def test_inner_triple_backtick_uses_quad(self):
        out = cf.fence("```js\nalert(1)\n```")
        assert out.startswith("````")
        assert out.endswith("````")

    def test_empty_returns_empty(self):
        assert cf.fence("") == ""
        assert cf.fence(None) == ""  # type: ignore[arg-type]

    def test_whitespace_only_returns_empty(self):
        assert cf.fence("   \n  ") == ""


class TestJoinList:
    def test_none(self):
        assert cf.join_list(None) == ""

    def test_list(self):
        assert cf.join_list(["a", "b", "c"]) == "a, b, c"

    def test_list_custom_sep(self):
        assert cf.join_list(["a", "b"], "; ") == "a; b"

    def test_scalar_passed_through(self):
        assert cf.join_list("a") == "a"
        assert cf.join_list(42) == "42"


class TestTruncate:
    def test_no_op_when_short(self):
        assert cf.truncate("hello", 100) == "hello"

    def test_truncates_with_ellipsis(self):
        out = cf.truncate("hello world", 5)
        assert len(out) == 5
        assert out.endswith("…")

    def test_zero_or_negative_max(self):
        assert cf.truncate("hello", 0) == "hello"
        assert cf.truncate("hello", -1) == "hello"


# --- Shipped example template ----------------------------------------------

REPO_ROOT = Path(__file__).resolve().parent.parent
EXAMPLE_TEMPLATE_PATH = REPO_ROOT / "comment.md.example"


@pytest.mark.skipif(
    not EXAMPLE_TEMPLATE_PATH.exists(),
    reason="comment.md.example not shipped at repo root",
)
class TestShippedExampleTemplate:
    """Regression guard for ``comment.md.example``.

    The example is a user-facing artifact: typos in the template break
    every custom-formatter user on the next release. These tests catch
    a broken shipped example before it ships.
    """

    @pytest.fixture
    def template_text(self):
        return EXAMPLE_TEMPLATE_PATH.read_text(encoding="utf-8")

    @pytest.fixture
    def formatter(self, template_text):
        return TemplateCommentFormatter(template_text)

    def _finding(self, **overrides):
        base = dict(_BASE_FINDING)
        base.update(overrides)
        return base

    def test_renders_minimal_finding(self, formatter, key):
        body = formatter.format(self._finding(), key=key, max_chars=20000)
        # Severity + title header.
        assert "🟠 major" in body
        assert "Credential Leak" in body
        # File + line.
        assert "src/logger.ts" in body
        assert "line 42" in body
        # Confidence + basis sub-line.
        assert "confidence: high" in body
        # Message body.
        assert "Token exposed in log." in body
        # Marker is always last line.
        assert body.rstrip().splitlines()[-1] == f"<!-- prb:{key} -->"

    def test_renders_with_evidence_and_suggestion(self, formatter, key):
        finding = self._finding(
            suggestion="logger.remove(token)",
            evidence={
                "whyNewInThisPr": "introduced here",
                "whyNotIntentional": "looks accidental",
                "changedLines": [10, 11, 12],
                "contextFilesRead": ["src/app.ts", "src/logger.ts"],
            },
        )
        body = formatter.format(finding, key=key, max_chars=20000)
        assert "introduced here" in body
        assert "looks accidental" in body
        assert "10, 11, 12" in body
        assert "src/app.ts, src/logger.ts" in body
        # Smart-fenced suggestion.
        assert "```\nlogger.remove(token)\n```" in body

    def test_renders_suggestion_with_inner_backticks_safely(self, formatter, key):
        # The smart fence filter in the template must handle a
        # suggestion that itself contains triple backticks.
        finding = self._finding(suggestion="```js\nalert(1)\n```")
        body = formatter.format(finding, key=key, max_chars=20000)
        # Outer fence is wider than inner so the block terminates.
        assert "````\n```js\nalert(1)\n```\n````" in body

    def test_renders_with_missing_optional_fields(self, formatter, key):
        finding = {
            "severity": "minor",
            "title": "Quiet nit",
            "message": "Tiny issue.",
            # No confidence, no contextBasis, no suggestion, no file, no line.
        }
        body = formatter.format(finding, key=key, max_chars=20000)
        assert "🟡 minor" in body
        assert "Quiet nit" in body
        assert "Tiny issue." in body
        # Missing optional fields render as empty / absent, never "None".
        assert "None" not in body
        # Marker line still present and last.
        assert body.rstrip().splitlines()[-1] == f"<!-- prb:{key} -->"

    def test_no_truncate_calls_in_shipped_template(self, template_text):
        # The shipped template must not call ``truncate`` — the
        # formatter handles truncation defensively and the template
        # should stay simple.
        assert "| truncate" not in template_text
        assert "| truncate(" not in template_text

