"""Narrow model-execution contract used by ReviewForge stages.

Every backend MUST keep ADO credentials out of child environments and restrict
model-side tools to read-only operations. It writes the model JSON response to
the supplied path; prompt, artifact, and validation contracts remain external.
"""
from __future__ import annotations

from pathlib import Path
from typing import Protocol

from ..config import Config, ConfigError


class ModelRunner(Protocol):
    """The complete model-runner surface consumed by engines and stages."""

    def run_json(self, prompt_path: Path, stdin_text: str, output_path: Path, stage: str) -> None: ...

    @property
    def token_usage(self) -> dict[str, int]: ...

    @property
    def last_tokens(self) -> dict[str, int]: ...

    @property
    def invocation_count(self) -> int: ...

    @property
    def repair_invocation_count(self) -> int: ...


def create_model_runner(cfg: Config) -> ModelRunner:
    """Create the configured model runner; only the Pi backend ships today."""
    if cfg.model_backend == "pi":
        from .runner import PiCliRunner

        return PiCliRunner(cfg)
    raise ConfigError(f"MODEL_BACKEND must be 'pi', got: {cfg.model_backend!r}")


__all__ = ["ModelRunner", "create_model_runner"]
