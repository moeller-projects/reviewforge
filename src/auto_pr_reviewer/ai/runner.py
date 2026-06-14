"""Pi coding-agent subprocess wrapper.

The Pi CLI is invoked as a side-effect-free read-only reviewer. Authentication
tokens for Azure DevOps are stripped from the subprocess environment to make
sure the model cannot exfiltrate them.
"""
from __future__ import annotations

from pathlib import Path
import json
import os
import subprocess
import sys

from ..config import Config


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


class PiRunner:
    """Run the ``pi`` CLI as a one-shot JSON producer."""

    def __init__(self, cfg: Config):
        self.cfg = cfg

    def run_json(self, prompt_path: Path, stdin_text: str, output_path: Path, stage: str) -> None:
        """Run Pi and write the JSON output to ``output_path``.

        On invalid JSON, retries once with a "return only JSON" repair prompt.
        Raises :class:`SystemExit` on timeouts or unrecoverable errors.
        """
        cmd = [
            "pi", "--no-session", "--no-context-files", "--no-extensions", "--no-skills",
            "--no-prompt-templates", "--tools", "read,grep", "--model", self.cfg.pi_model,
            "--thinking", "medium", "--append-system-prompt", str(prompt_path), "-p",
            "Process the task described in the system prompt. The instruction and unified diff are provided on stdin.",
        ]
        _log(f"running Pi {stage} (timeout: {self.cfg.pi_timeout_secs}s)")
        env = os.environ.copy()
        # Defense in depth: never let Pi see ADO tokens.
        env.pop("ADO_AUTH_TOKEN", None)
        env.pop("ADO_MCP_AUTH_TOKEN", None)
        env.pop("ADO_API_KEY", None)
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
            for line in cp.stderr.decode(errors="replace").splitlines():
                _log(f"[pi {stage}] {line}")
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

        # Repair attempt: ask Pi to return only JSON.
        repair = cmd[:-2] + [
            "-p",
            "Your previous response was not valid JSON. Return only the JSON object – no markdown fences, no prose.",
        ]
        cp = subprocess.run(
            repair,
            input=stdin_text.encode(),
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
            timeout=self.cfg.pi_timeout_secs,
            env=env,
        )
        if cp.returncode or not cp.stdout:
            raise SystemExit(f"[review][ERROR] Pi {stage} repair call failed")
        output_path.write_bytes(cp.stdout)
        strip_json_fences(output_path)
        try:
            json.loads(output_path.read_text())
        except Exception:
            raise SystemExit(f"[review][ERROR] pi {stage} repair call produced invalid JSON")


__all__ = ["PiRunner", "strip_json_fences"]
