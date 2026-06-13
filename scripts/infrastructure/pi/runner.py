from __future__ import annotations

import json, os, subprocess
from pathlib import Path

from config import Config


def strip_json_fences(path: Path) -> None:
    lines = [line for line in path.read_text(encoding="utf-8").splitlines() if not line.strip().startswith("```")]
    path.write_text("\n".join(lines).strip() + "\n", encoding="utf-8")


class PiRunner:
    def __init__(self, cfg: Config):
        self.cfg = cfg

    def run_json(self, prompt_path: Path, stdin_text: str, output_path: Path, stage: str) -> None:
        cmd = [
            "pi", "--no-session", "--no-context-files", "--no-extensions", "--no-skills",
            "--no-prompt-templates", "--tools", "read,grep", "--model", self.cfg.pi_model,
            "--thinking", "medium", "--append-system-prompt", str(prompt_path), "-p",
            "Process the task described in the system prompt. The instruction and unified diff are provided on stdin.",
        ]
        print(f"[review] running Pi {stage} (timeout: {self.cfg.pi_timeout_secs}s)", file=__import__('sys').stderr)
        env = os.environ.copy()
        env.pop("ADO_AUTH_TOKEN", None)
        env.pop("ADO_MCP_AUTH_TOKEN", None)
        try:
            cp = subprocess.run(cmd, input=stdin_text.encode(), stdout=subprocess.PIPE, stderr=subprocess.PIPE, timeout=self.cfg.pi_timeout_secs, env=env)
        except subprocess.TimeoutExpired:
            raise SystemExit(f"[review][ERROR] Pi {stage} timed out after {self.cfg.pi_timeout_secs}s")
        if cp.stderr:
            for line in cp.stderr.decode(errors='replace').splitlines():
                print(f"[review][pi {stage}] {line}", file=__import__('sys').stderr)
        if cp.returncode:
            raise SystemExit(f"[review][ERROR] pi {stage} exited {cp.returncode}")
        output_path.write_bytes(cp.stdout)
        if not output_path.stat().st_size:
            raise SystemExit(f"[review][ERROR] pi {stage} produced no output")
        try:
            json.loads(output_path.read_text())
            return
        except Exception:
            strip_json_fences(output_path)
        try:
            json.loads(output_path.read_text())
            return
        except Exception:
            repair = cmd[:-2] + ["-p", "Your previous response was not valid JSON. Return only the JSON object – no markdown fences, no prose."]
            cp = subprocess.run(repair, input=stdin_text.encode(), stdout=subprocess.PIPE, stderr=subprocess.PIPE, timeout=self.cfg.pi_timeout_secs, env=env)
            if cp.returncode or not cp.stdout:
                raise SystemExit(f"[review][ERROR] Pi {stage} repair call failed")
            output_path.write_bytes(cp.stdout)
            strip_json_fences(output_path)
            try:
                json.loads(output_path.read_text())
            except Exception:
                raise SystemExit(f"[review][ERROR] pi {stage} repair call produced invalid JSON")
