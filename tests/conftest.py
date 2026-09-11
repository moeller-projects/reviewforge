"""Shared test fixtures for ReviewForge.

Provides :func:`FakePopen` and :func:`popen_factory` so tests can stub the
streaming ``PiCliRunner._run_process`` subprocess seam with a minimal
``Popen``-like object instead of a real process.
"""
from __future__ import annotations

import io
import subprocess
from typing import Any, Callable


class _FakeStdin:
    def __init__(self) -> None:
        self.data = b""

    def write(self, data: bytes) -> int:
        self.data += data
        return len(data)

    def close(self) -> None:
        pass



class FakePopen:
    """Minimal stand-in for ``subprocess.Popen``.

    ``stdout`` and ``stderr`` are in-memory byte streams; ``stderr`` is
    iterable line-by-line so the runner's streaming thread terminates cleanly.
    ``wait`` returns ``returncode`` unless ``timeout`` is set, in which case it
    raises ``subprocess.TimeoutExpired`` (mimicking a slow process).
    """

    def __init__(
        self,
        cmd: list[str],
        *,
        returncode: int = 0,
        stdout: bytes = b"",
        stderr: bytes = b"",
        timeout: float | None = None,
        env: dict[str, str] | None = None,
        cwd: str | None = None,
    ) -> None:
        self.cmd = list(cmd)
        self.returncode = returncode
        self.stdin = _FakeStdin()
        self.stdout = io.BytesIO(stdout)
        self.stderr = io.BytesIO(stderr)
        self.timeout = timeout
        self.env = env or {}
        self.cwd = cwd

    def wait(self, timeout: float | None = None) -> int:
        if self.timeout is not None:
            raise subprocess.TimeoutExpired(self.cmd, self.timeout)
        return self.returncode

    def kill(self) -> None:
        self.returncode = -9
        self.timeout = None


def popen_factory(
    responses: list[tuple[int, bytes, bytes]] | Callable[..., tuple[int, bytes, bytes]],
    *,
    timeout: float | None = None,
) -> Callable[..., FakePopen]:
    """Build a ``Popen`` factory that replays ``(returncode, stdout, stderr)``.

    ``responses`` may be a list consumed in order, or a callable returning the
    next tuple. The returned factory records each constructed :class:`FakePopen`
    in ``calls`` for assertions and captures ``env``/``cwd`` keywords.
    """
    if callable(responses):
        responder = responses
    else:
        it = iter(responses)
        responder = lambda: next(it)  # noqa: E731

    calls: list[FakePopen] = []

    def factory(cmd, **kwargs: Any) -> FakePopen:
        returncode, stdout, stderr = responder()
        proc = FakePopen(
            cmd,
            returncode=returncode,
            stdout=stdout,
            stderr=stderr,
            timeout=timeout,
            env=kwargs.get("env"),
            cwd=kwargs.get("cwd"),
        )
        calls.append(proc)
        return proc

    factory.calls = calls  # type: ignore[attr-defined]
    return factory
