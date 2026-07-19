"""Reasoning engines for ReviewForge.

A reasoning engine encapsulates the model-driven portion of the review
pipeline. The orchestrator selects an engine by name and delegates the
review work to it.
"""
from __future__ import annotations

from .engine import ReasoningEngine, get_engine, register_engine
from .multi_stage import MultiStageReasoningEngine  # registers itself
from .single_pi import SinglePiReasoningEngine  # registers itself

__all__ = [
    "ReasoningEngine",
    "get_engine",
    "register_engine",
    "MultiStageReasoningEngine",
    "SinglePiReasoningEngine",
]
