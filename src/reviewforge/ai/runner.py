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
import hashlib
import json
import os
import re
import subprocess
import sys
import tempfile
import threading
import time

from ..config import Config, _ENV_ALIASES
from ..exceptions import PiExecutionError
from ..runlog import info as _log, warning as _warn, SECRET_NAMES
from .prompts import augment_prompt_file


#: Regex to find Pi's token-usage lines on stderr.
_TOKEN_RE = re.compile(
    r"tokens?[:\s]+(?P<in>\d+)\s*(?:in|input)?\s*[/,]\s*(?P<out>\d+)\s*(?:out|output)?",
    re.IGNORECASE,
)

#: Best-effort extraction of Pi read-tool paths from stderr diagnostics.
_CONTEXT_PATH_RE = re.compile(r"\.reviewforge-context/(?P<file>[A-Za-z0-9._/-]+)", re.IGNORECASE)
_READ_TOOL_RE = re.compile(r"\bread\b", re.IGNORECASE)


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

def _scrub_ado_env(env: dict[str, str]) -> None:
    """Remove ADO credentials from the subprocess env in place."""
    for key in dict.fromkeys((*_ENV_ALIASES.get("ado_token", ()), "AZURE_DEVOPS_EXT_PAT")):
        env.pop(key, None)


def _parse_context_file_reads(stderr_text: str) -> dict[str, int] | str:
    """Count readable-root context files mentioned by Pi read diagnostics."""
    if not stderr_text:
        return "unknown"
    saw_read_diagnostic = False
    counts: dict[str, int] = {}
    for line in stderr_text.splitlines():
        if not _READ_TOOL_RE.search(line):
            continue
        saw_read_diagnostic = True
        for match in _CONTEXT_PATH_RE.finditer(line):
            name = f".reviewforge-context/{match.group('file').rstrip('.,;:)]')}"
            counts[name] = counts.get(name, 0) + 1
    return counts if saw_read_diagnostic else "unknown"


def _redact(text: str) -> str:
    """Replace known secret values with ``***`` (defense in depth)."""
    for name in SECRET_NAMES:
        value = os.environ.get(name)
        if value:
            text = text.replace(value, "***")
    return text


def _stderr_tail(stderr_bytes: bytes, *, max_lines: int = 3) -> str:
    """Return the last non-empty stderr lines, for surfacing in errors."""
    if not stderr_bytes:
        return ""
    lines = [
        line.strip()
        for line in stderr_bytes.decode(errors="replace").splitlines()
        if line.strip()
    ]
    return _redact(" | ".join(lines[-max_lines:]))


def _default_session_id(cfg: Config) -> str:
    """Build a session id that re-runs on the same PR can resume."""
    if cfg.review_run_id:
        return f"pr-{cfg.pr_id}-review-{cfg.review_run_id}"
    return f"pr-{cfg.pr_id}-review"


def _prompt_candidate_matches(candidate: Path | str | None, resolved: Path) -> bool:
    if candidate is None:
        return False
    try:
        return Path(candidate).resolve() == resolved
    except OSError:
        return Path(candidate) == resolved


def _review_prompt_candidates(cfg: Config) -> tuple[Path | str | None, ...]:
    return (
        getattr(cfg, "fast_review_prompt_path", None),
        getattr(cfg, "review_prompt_path", None),
    )


def _is_review_prompt(prompt_path: Path, cfg: Config) -> bool:
    """Return True when ``prompt_path`` is one of the review prompts."""
    try:
        resolved = prompt_path.resolve()
    except OSError:
        resolved = prompt_path
    return any(
        _prompt_candidate_matches(candidate, resolved)
        for candidate in _review_prompt_candidates(cfg)
    )

class PiCliRunner:
    """Run the ``pi`` CLI as a JSON producer, with optional session reuse."""

    def __init__(self, cfg: Config):
        self.cfg = cfg
        self._last_tokens: dict[str, int] = {}
        self._token_usage = {"in": 0, "out": 0, "total": 0}
        self._token_usage_source = "none"
        self._invocation_count = 0
        self._repair_invocation_count = 0
        self._working_dir: Path | None = None
        self._context_file_reads: dict[str, int] | str = {}
        #: Ordered per-invocation outcome records, one per ``_run_process``
        #: subprocess call (primary attempts and repair calls alike).
        self._invocations: list[dict[str, object]] = []
        # Per-runner cache of source prompt path → augmented prompt path.
        # The augmented copy has the LANGUAGE directive appended so every
        # stage (review, verify, severity, intent, plan, digest) instructs
        # the model in the configured review_language. Using a private
        # temp dir keeps augmented files out of the read-only prompts dir
        # shipped inside the container.
        self._prompt_cache: dict[Path, Path] = {}
        self._prompt_dir = Path(tempfile.mkdtemp(prefix="pr-review-prompts-"))

    @property
    def last_tokens(self) -> dict[str, int]:
        """Tokens reported by the most recent Pi call."""
        return dict(self._last_tokens)

    @property
    def token_usage(self) -> dict[str, int]:
        """Tokens accumulated across all calls made by this runner."""
        return dict(self._token_usage)

    @property
    def token_usage_source(self) -> str:
        """Source of token usage from the most recent Pi call."""
        return self._token_usage_source

    @property
    def invocation_count(self) -> int:
        return self._invocation_count

    @property
    def repair_invocation_count(self) -> int:
        return self._repair_invocation_count

    @property
    def invocations(self) -> list[dict[str, object]]:
        """Ordered per-invocation outcome records."""
        return list(self._invocations)

    @property
    def context_file_reads(self) -> dict[str, int] | str:
        """Best-effort counts of staged context files read by Pi."""
        if isinstance(self._context_file_reads, dict):
            return dict(self._context_file_reads)
        return self._context_file_reads

    def set_working_dir(self, path: Path | None) -> None:
        """Set the explicit cwd used by review and repair subprocesses."""
        self._working_dir = Path(path) if path else None

    def _record_context_reads(self, reads: dict[str, int] | str) -> None:
        if reads == "unknown":
            self._context_file_reads = "unknown"
            return
        if self._context_file_reads == "unknown":
            return
        for path, count in reads.items():
            self._context_file_reads[path] = self._context_file_reads.get(path, 0) + count

    def _record_tokens(self, tokens: dict[str, int]) -> None:
        self._last_tokens = dict(tokens)
        self._token_usage_source = "stderr-regex" if tokens else "none"
        for key in ("in", "out", "total"):
            self._token_usage[key] += int(tokens.get(key, 0) or 0)

    def _warn_missing_token_usage(self, stage: str) -> None:
        """Warn when a successful Pi response lacks observable usage."""
        if not self._last_tokens:
            _warn(f"Pi {stage} returned a non-empty response without token usage")
        if self._invocation_count and not any(self._token_usage.values()):
            _warn(
                f"Pi has {self._invocation_count} invocation(s) "
                "with all parsed token usage values at 0"
            )

    @property
    def session_id(self) -> str:
        return self.cfg.pi_session_id or _default_session_id(self.cfg)

    def _resolve_system_prompt(self, prompt_path: Path) -> Path:
        """Return a system-prompt path with standards and/or the directive.

        Review prompts (the fast single-pi prompt and the legacy review
        prompt) also receive the configured coding standards. Pi only reads
        the prompt file once per call, so we materialize the augmented
        version on disk and cache it for the lifetime of this runner.
        Caching is keyed on the source ``Path`` (not its contents) because
        the source prompt files are static templates shipped with the
        package.
        """
        cached = self._prompt_cache.get(prompt_path)
        if cached is not None:
            return cached
        include_standards = self._is_review_prompt(prompt_path)
        path_hash = hashlib.sha1(str(prompt_path.resolve()).encode("utf-8")).hexdigest()[:8]
        dest = self._prompt_dir / f"{prompt_path.stem}.{path_hash}.lang.md"
        augmented = augment_prompt_file(
            prompt_path, self.cfg, dest=dest, include_standards=include_standards
        )
        self._prompt_cache[prompt_path] = augmented
        return augmented

    def _is_review_prompt(self, prompt_path: Path) -> bool:
        return _is_review_prompt(prompt_path, self.cfg)

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
            "--thinking", getattr(self.cfg, "pi_thinking", "medium"),
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

    def _record_invocation(
        self,
        *,
        stage: str,
        repair: bool,
        attempt: int | None,
        returncode: int | None,
        timed_out: bool,
        stdout_bytes: int,
        stdout_text: str,
        stderr_bytes: bytes,
        tokens: dict[str, int],
        duration_ms: int,
    ) -> None:
        """Append one per-invocation outcome record to the run log."""
        self._invocations.append(
            {
                "stage": stage,
                "repair": repair,
                "attempt": attempt,
                "returncode": returncode,
                "timed_out": timed_out,
                "stdout_bytes": stdout_bytes,
                "response": stdout_text,
                "stderr_tail": _stderr_tail(stderr_bytes),
                "tokens_in": tokens.get("in", 0),
                "tokens_out": tokens.get("out", 0),
                "tokens_total": tokens.get("total", 0),
                "duration_ms": duration_ms,
            }
        )

    def _run_process(
        self,
        cmd: list[str],
        input_data: bytes,
        stage: str,
        env: dict[str, str],
        *,
        repair: bool = False,
        attempt: int | None = None,
    ) -> subprocess.CompletedProcess[bytes]:
        self._invocation_count += 1
        started = time.monotonic()
        marker = " repair (in session)" if repair and self.cfg.pi_session_enabled else " repair" if repair else ""
        proc = subprocess.Popen(
            cmd,
            stdin=subprocess.PIPE,
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
            env=env,
            **({"cwd": str(self._working_dir)} if self._working_dir else {}),
        )
        stdout_bytes, stderr_text, timed_out = self._pump_pipes(proc, input_data, stage, marker)
        duration_ms = int((time.monotonic() - started) * 1000)
        if timed_out:
            self._record_timeout(
                stage=stage,
                repair=repair,
                attempt=attempt,
                stdout_bytes=stdout_bytes,
                stderr_text=stderr_text,
                duration_ms=duration_ms,
            )
        tokens = self._parse_token_usage(stderr_text)
        self._record_context_reads(_parse_context_file_reads(stderr_text))
        self._record_tokens(tokens)
        self._record_invocation(
            stage=stage,
            repair=repair,
            attempt=attempt,
            returncode=proc.returncode,
            timed_out=False,
            stdout_bytes=len(stdout_bytes),
            stdout_text=stdout_bytes.decode(errors="replace"),
            stderr_bytes=stderr_text.encode(errors="replace"),
            tokens=tokens,
            duration_ms=duration_ms,
        )
        return subprocess.CompletedProcess(cmd, proc.returncode, stdout_bytes, stderr_text.encode(errors="replace"))

    def _pump_pipes(
        self,
        proc: subprocess.Popen[bytes],
        input_data: bytes,
        stage: str,
        marker: str,
    ) -> tuple[bytes, str, bool]:
        """Drain both output pipes while stdin is being sent; enforce the timeout.

        Pi can produce enough diagnostics or response data to fill a pipe
        before consuming all of a large review input, so a synchronous stdin
        write here would deadlock both processes.
        """
        stderr_lines: list[str] = []
        stdout_holder: list[bytes] = []

        def _write_stdin() -> None:
            assert proc.stdin is not None
            try:
                proc.stdin.write(input_data)
            except BrokenPipeError:
                pass
            finally:
                try:
                    proc.stdin.close()
                except BrokenPipeError:
                    pass

        # Stream stderr lines to the run log as they arrive, while
        # accumulating them for post-hoc token/context parsing.
        def _stream_stderr() -> None:
            assert proc.stderr is not None
            for raw in proc.stderr:
                line = raw.decode(errors="replace").rstrip("\n")
                stderr_lines.append(line)
                _log(f"[pi {stage}{marker}] {line}")

        def _drain_stdout() -> None:
            assert proc.stdout is not None
            stdout_holder.append(proc.stdout.read())

        threads = [
            threading.Thread(target=target, daemon=True)
            for target in (_stream_stderr, _drain_stdout, _write_stdin)
        ]
        for thread in threads:
            thread.start()

        timed_out = False
        try:
            proc.wait(timeout=self.cfg.pi_timeout_secs)
        except subprocess.TimeoutExpired:
            timed_out = True
            proc.kill()
            proc.wait()
        threads[0].join()
        threads[1].join()
        threads[2].join(timeout=1)
        stdout_bytes = stdout_holder[0] if stdout_holder else b""
        return stdout_bytes, "\n".join(stderr_lines), timed_out


    def _record_timeout(
        self,
        *,
        stage: str,
        repair: bool,
        attempt: int | None,
        stdout_bytes: bytes,
        stderr_text: str,
        duration_ms: int,
    ) -> None:
        """Record a timed-out invocation and raise the corresponding error."""
        self._record_invocation(
            stage=stage,
            repair=repair,
            attempt=attempt,
            returncode=None,
            timed_out=True,
            stdout_bytes=len(stdout_bytes),
            stdout_text=stdout_bytes.decode(errors="replace"),
            stderr_bytes=stderr_text.encode(errors="replace"),
            tokens={},
            duration_ms=duration_ms,
        )
        message = (
            f"[review][ERROR] Pi {stage} repair timed out"
            if repair
            else f"[review][ERROR] Pi {stage} timed out after {self.cfg.pi_timeout_secs}s"
        )
        raise PiExecutionError(message, details={"stage": stage, "repair": repair})


    def _run_primary_with_retry(
        self,
        cmd: list[str],
        input_data: bytes,
        stage: str,
        env: dict[str, str],
    ) -> subprocess.CompletedProcess[bytes]:
        """Run the primary Pi call, retrying transient non-zero exits.

        A non-zero exit (e.g. Pi "terminated" after failing to find or create
        its session) is retried up to ``cfg.pi_retry_attempts`` times with
        exponential backoff. Timeouts and empty output are not retried here:
        the former is surfaced by :meth:`_run_process` and the latter by
        :meth:`run_json`.
        """
        attempts = max(1, self.cfg.pi_retry_attempts)
        last: subprocess.CompletedProcess[bytes] | None = None
        for attempt in range(1, attempts + 1):
            cp = self._run_process(cmd, input_data, stage, env, attempt=attempt)
            if cp.returncode == 0:
                return cp
            last = cp
            if attempt < attempts:
                delay = min(
                    self.cfg.pi_retry_cap_delay,
                    self.cfg.pi_retry_base_delay * (2 ** (attempt - 1)),
                )
                _warn(
                    f"Pi {stage} exited {cp.returncode} "
                    f"(attempt {attempt}/{attempts}); retrying in {delay:.1f}s"
                )
                time.sleep(delay)
        return last  # type: ignore[return-value]

    def run_json(
        self,
        prompt_path: Path,
        stdin_text: str,
        output_path: Path,
        stage: str,
    ) -> None:
        """Run Pi and write the JSON output to ``output_path``."""
        instruction = "Process the task described in the system prompt. The instruction and unified diff are provided on stdin."
        resolved_prompt = self._resolve_system_prompt(prompt_path)
        sid = self.session_id if self.cfg.pi_session_enabled else "<no-session>"
        _log(
            f"running Pi {stage} (timeout: {self.cfg.pi_timeout_secs}s, "
            f"session: {sid}, clear: {self.cfg.pi_session_clear})"
        )
        env = self._build_subprocess_env()
        cp = self._run_primary_with_retry(
            self._build_cmd(resolved_prompt, instruction),
            stdin_text.encode(),
            stage,
            env,
        )
        if cp.returncode:
            tail = _stderr_tail(cp.stderr)
            message = f"[review][ERROR] pi {stage} exited {cp.returncode}"
            if tail:
                message += f" (stderr: {tail})"
            raise PiExecutionError(
                message,
                details={"stage": stage, "returncode": cp.returncode, "stderr_tail": tail},
            )
        output_path.write_bytes(cp.stdout)
        if not output_path.stat().st_size:
            raise PiExecutionError(f"[review][ERROR] pi {stage} produced no output", details={"stage": stage})
        self._warn_missing_token_usage(stage)
        if self._valid_json(output_path):
            return
        self._run_repair(resolved_prompt, stdin_text, stage, env, output_path)

    def _valid_json(self, output_path: Path) -> bool:
        """Return ``True`` if ``output_path`` holds valid JSON, stripping fences once."""
        try:
            json.loads(output_path.read_text())
            return True
        except Exception:
            strip_json_fences(output_path)
        try:
            json.loads(output_path.read_text())
            return True
        except Exception:
            return False

    def _run_repair(
        self,
        resolved_prompt: Path,
        stdin_text: str,
        stage: str,
        env: dict[str, str],
        output_path: Path,
    ) -> None:
        """Re-ask Pi for valid JSON in the same session after a bad response."""
        _log(
            f"running Pi {stage} repair ({'in session' if self.cfg.pi_session_enabled else 'legacy mode'})"
        )
        self._repair_invocation_count += 1
        cp = self._run_process(
            self._build_cmd(
                resolved_prompt,
                "Your previous response was not valid JSON. Return only the JSON object – no markdown fences, no prose.",
            ),
            b"" if self.cfg.pi_session_enabled else stdin_text.encode(),
            stage,
            env,
            repair=True,
        )
        if cp.returncode or not cp.stdout:
            raise PiExecutionError(
                f"[review][ERROR] Pi {stage} repair call failed",
                details={"stage": stage, "repair": True, "returncode": cp.returncode},
            )
        output_path.write_bytes(cp.stdout)
        self._warn_missing_token_usage(f"{stage} repair")
        if not self._valid_json(output_path):
            raise PiExecutionError(
                f"[review][ERROR] pi {stage} repair call produced invalid JSON",
                details={"stage": stage, "repair": True},
            )


PiRunner = PiCliRunner


__all__ = ["PiCliRunner", "PiRunner", "strip_json_fences"]
