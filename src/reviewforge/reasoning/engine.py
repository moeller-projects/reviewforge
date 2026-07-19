"""Reasoning Engine abstraction.

A ReasoningEngine encapsulates the Pi-driven portion of the review pipeline.
The orchestrator builds a small physical pipeline (metadata, repo prep,
engine, post) and the engine decides how to reason: multi-stage, single
Pi call, or a different provider entirely.
"""
from __future__ import annotations

from abc import ABC, abstractmethod
from typing import Any

from ..pipeline.schemas import ReviewResult
from ..pipeline.stage import StageContext


class ReasoningEngine(ABC):
    """Abstract base for review reasoning implementations."""

    @property
    @abstractmethod
    def name(self) -> str:
        """Stable identifier for this engine (used in run summaries)."""
        ...

    @abstractmethod
    def execute(self, ctx: StageContext) -> ReviewResult:
        """Run the reasoning loop and return a structured review result.

        The engine may write intermediate artifacts for observability,
        but it must always return a validated ``ReviewResult``.
        """
        ...


#: name -> engine class
_ENGINE_REGISTRY: dict[str, type[ReasoningEngine]] = {}


def register_engine(name: str, cls: type[ReasoningEngine]) -> None:
    """Register a concrete engine implementation."""
    _ENGINE_REGISTRY[name] = cls


def get_engine(name: str, *args: Any, **kwargs: Any) -> ReasoningEngine:
    """Instantiate the engine identified by ``name``.

    Raises :class:`ValueError` if the engine is unknown.
    """
    cls = _ENGINE_REGISTRY.get(name)
    if cls is None:
        raise ValueError(f"Unknown reasoning engine: {name!r}")
    return cls(*args, **kwargs)


__all__ = ["ReasoningEngine", "get_engine", "register_engine"]


# Register the built-in engines. Keep these imports at the bottom to avoid
# circular imports while the engine package is being initialized.
from .multi_stage import MultiStageReasoningEngine  # noqa: E402,F401
from .single_pi import SinglePiReasoningEngine  # noqa: E402,F401
