"""Structured domain exceptions for ReviewForge."""
from __future__ import annotations


class ReviewForgeError(RuntimeError):
    """Base class for all ReviewForge domain errors."""

    def __init__(self, message: str, *, details: dict[str, object] | None = None) -> None:
        super().__init__(message)
        self.message = message
        self.details = details or {}

    def __str__(self) -> str:  # pragma: no cover - exercised through repr
        if self.details:
            return f"{self.message} ({self.details})"
        return self.message


class ReasoningEngineError(ReviewForgeError):
    """Raised when a reasoning engine fails to produce a valid review result."""


class SchemaValidationError(ReviewForgeError):
    """Raised when a model response does not validate against the expected schema."""


class ProjectionError(ReviewForgeError):
    """Raised when a canonical result cannot be projected into a legacy shape."""


__all__ = [
    "ProjectionError",
    "ReasoningEngineError",
    "ReviewForgeError",
    "SchemaValidationError",
]
