"""Pi coding-agent subprocess wrapper.

The Pi CLI is invoked as a side-effect-free read-only reviewer. Authentication
tokens for Azure DevOps are stripped from the subprocess environment to make
sure the model cannot exfiltrate them.

Session reuse (Phases A + D + E of the token-savings plan):

* By default, the runner uses ``--session-id <id>`` so the model keeps
  the system prompt, the diff, and prior turn context between stage
  calls. The first stage pays the full context cost; subsequent stages
  (plan, digest, review chunks, verify, severity) just send the new
  instruction and the per-chunk diff.
* ``--session-id`` is preferred over ``--session <id>`` because it
  *creates* the session if it doesn't exist yet, whereas ``--session``
  errors on a missing id (which would be the case on the first stage
  call). This keeps the per-PR session deterministic even when the
  runner is invoked from scratch.
* ``cfg.pi_session_enabled=False`` falls back to ``--no-session`` for
  deterministic reruns.
* ``cfg.pi_session_clear=True`` passes ``--clear-session`` to start a
  fresh session under the same id (e.g. after a schema change or a
  corrupted prior state).
* The default session id is ``pr-{pr_id}-review-{run_id}`` so re-runs
  on the same PR resume the same conversation.

When the model returns invalid JSON, the repair call also runs in the
same session, asking only for ``return only JSON`` instead of resending
the full context.
"""
from __future__ import annotations

from pathlib import Path
import json
import os
import re
import subprocess
import sys

from ..config import Config


#: Regex to find Pi's token-usage lines on stderr.
_TOKEN_RE = re.compile(
    r"tokens?[:\s]+(?P<in>\d+)\s*(?:in|input)?\s*[/,]\s*(?P<out>\d+)\s*(?:out|output)?",
    re.IGNORECASE,
)


def strip_json_fences(path: Path) -> None:
    """Remove Markdown code fences from a JSON file in place.

    Pi occasionally wraps its JSON output in triple-backtick fences. This
    helper strips the leading fence line(s) so a downstream JSON parser
    does not have to deal with them.
    """
    lines = [
        line for line in path.read_text(encoding="utf-8").splitlines()
        if not line.strip().startswith("```")
    ]
    path.write_text("\n".join(lines).strip() + "\n", encoding="utf-8")


def _log(message: str) -> None:
    print(f"[review] {message}", file=sys.stderr)


def _scrub_ado_env(env: dict[str, str]) -> None:
    """Remove ADO credentials from the subprocess env in place."""
    for key in ("ADO_AUTH_TOKEN", "ADO_MCP_AUTH_TOKEN", "ADO_API_KEY"):
        env.pop(key, None)


def _default_session_id(cfg: Config) -> str:
    """Build a session id that re-runs on the same PR can resume."""
    if cfg.review_run_id:
        return f"pr-{cfg.pr_id}-review-{cfg.review_run_id}"
    return f"pr-{cfg.pr_id}-review"


class PiRunner:
    """Run the ``pi`` CLI as a JSON producer, with optional session reuse."""

    def __init__(self, cfg: Config):
        self.cfg = cfg
        self._last_tokens: dict[str, int] = {}

    @property
    def last_tokens(self) -> dict[str, int]:
        """Tokens reported by the most recent Pi call (``in``/``out``/``total``)."""
        return dict(self._last_tokens)

    @property
    def session_id(self) -> str:
        return self.cfg.pi_session_id or _default_session_id(self.cfg)

    def _build_cmd(self, prompt_path: Path, instruction: str) -> list[str]:
        """Compose the Pi CLI command, including session flags when enabled."""
        cmd = [
            "pi",
            *(["--no-session"] if not self.cfg.pi_session_enabled else []),
            *(["--session-id", self.session_id] if self.cfg.pi_session_enabled else []),
            *(["--clear-session"] if self.cfg.pi_session_clear else []),
            "--no-context-files",
            "--no-extensions",
            "--no-skills",
            "--no-prompt-templates",
            "--tools", "read,grep",
            "--model", self.cfg.pi_model,
            "--thinking", "medium",
            "--append-system-prompt", str(prompt_path),
            "-p", instruction,
        ]
        return cmd

    def _build_subprocess_env(self) -> dict[str, str]:
        env = os.environ.copy()
        # Defense in depth: never let Pi see ADO tokens.
        _scrub_ado_env(env)
        return env

    @staticmethod
    def _parse_token_usage(stderr_text: str) -> dict[str, int]:
        """Best-effort parse of Pi's token-usage output on stderr."""
        result: dict[str, int] = {}
        for line in stderr_text.splitlines():
            m = _TOKEN_RE.search(line)
            if m:
                try:
                    result["in"] = int(m.group("in"))
                    result["out"] = int(m.group("out"))
                    result["total"] = result["in"] + result["out"]
                except (TypeError, ValueError):
                    pass
                break
        return result

    def run_json(
        self,
        prompt_path: Path,
        stdin_text: str,
        output_path: Path,
        stage: str,
    ) -> None:
        """Run Pi and write the JSON output to ``output_path``.

        On invalid JSON, retries once with a "return only JSON" repair
        prompt that runs in the same session (no re-sending of context).

        Raises :class:`SystemExit` on timeouts or unrecoverable errors.
        """
        if not self.cfg.pi_session_enabled:
            instruction = (
                "Process the task described in the system prompt. "
                "The instruction and unified diff are provided on stdin."
            )
        else:
            # Sessions: the first call still gets the full stdin payload
            # (system prompt context, diff, etc). Later calls send only
            # the new instruction because the model retains prior context.
            instruction = (
                "Process the task described in the system prompt. "
                "The instruction and unified diff are provided on stdin."
            )
        cmd = self._build_cmd(prompt_path, instruction)
        env = self._build_subprocess_env()

        sid = self.session_id if self.cfg.pi_session_enabled else "<no-session>"
        _log(
            f"running Pi {stage} (timeout: {self.cfg.pi_timeout_secs}s, "
            f"session: {sid}, clear: {self.cfg.pi_session_clear})"
        )
        try:
            cp = subprocess.run(
                cmd,
                input=stdin_text.encode(),
                stdout=subprocess.PIPE,
                stderr=subprocess.PIPE,
                timeout=self.cfg.pi_timeout_secs,
                env=env,
            )
        except subprocess.TimeoutExpired:
            raise SystemExit(
                f"[review][ERROR] Pi {stage} timed out after {self.cfg.pi_timeout_secs}s"
            )
        if cp.stderr:
            stderr_text = cp.stderr.decode(errors="replace")
            for line in stderr_text.splitlines():
                _log(f"[pi {stage}] {line}")
            self._last_tokens = self._parse_token_usage(stderr_text)
        else:
            self._last_tokens = {}
        if cp.returncode:
            raise SystemExit(f"[review][ERROR] pi {stage} exited {cp.returncode}")
        output_path.write_bytes(cp.stdout)
        if not output_path.stat().st_size:
            raise SystemExit(f"[review][ERROR] pi {stage} produced no output")

        # First attempt: parse as-is.
        try:
            json.loads(output_path.read_text())
            return
        except Exception:
            strip_json_fences(output_path)
        try:
            json.loads(output_path.read_text())
            return
        except Exception:
            pass

        # Repair attempt:
        # - In session mode, the model already has the original context, so
        #   we send empty stdin and just ask it to return only JSON.
        # - In legacy / no-session mode, the model has no memory of the
        #   original payload, so we resend it.
        repair = self._build_cmd(
            prompt_path,
            "Your previous response was not valid JSON. "
            "Return only the JSON object – no markdown fences, no prose.",
        )
        repair_input = b"" if self.cfg.pi_session_enabled else stdin_text.encode()
        _log(
            f"running Pi {stage} repair ({'in session' if self.cfg.pi_session_enabled else 'legacy mode'})"
        )
        try:
            cp = subprocess.run(
                repair,
                input=repair_input,
                stdout=subprocess.PIPE,
                stderr=subprocess.PIPE,
                timeout=self.cfg.pi_timeout_secs,
                env=env,
            )
        except subprocess.TimeoutExpired:
            raise SystemExit(
                f"[review][ERROR] Pi {stage} repair timed out"
            )
        if cp.stderr:
            stderr_text = cp.stderr.decode(errors="replace")
            for line in stderr_text.splitlines():
                _log(f"[pi {stage} repair] {line}")
            # Update last_tokens from the repair call's stderr as well.
            parsed = self._parse_token_usage(stderr_text)
            if parsed:
                self._last_tokens = parsed
        if cp.returncode or not cp.stdout:
            raise SystemExit(f"[review][ERROR] Pi {stage} repair call failed")
        output_path.write_bytes(cp.stdout)
        strip_json_fences(output_path)
        try:
            json.loads(output_path.read_text())
        except Exception:
            raise SystemExit(f"[review][ERROR] pi {stage} repair call produced invalid JSON")


__all__ = ["PiRunner", "strip_json_fences"]
