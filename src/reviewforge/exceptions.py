"""Structured domain exceptions for ReviewForge."""
from __future__ import annotations
import sys


class ReviewForgeError(RuntimeError):
    """Base class for all ReviewForge domain errors."""

    def __init__(self, message: str, *, details: dict[str, object] | None = None) -> None:
        super().__init__(message)
        self.message = message
        self.details = details or {}

    def __str__(self) -> str:
        return self.message


def emit_domain_error(exc: "ReviewForgeError") -> None:
    """Print a domain error using the stable operator-facing format."""
    print(exc.message, file=sys.stderr)
    response_body = exc.details.get("response_body")
    if response_body:
        print(f"[review][ERROR] ADO response body: {response_body}", file=sys.stderr)


class ReasoningEngineError(ReviewForgeError):
    """Raised when a reasoning engine fails to produce a valid review result."""


class PiExecutionError(ReviewForgeError):
    """Raised when Pi cannot produce the requested output."""


class GitOperationError(ReviewForgeError):
    """Raised when a Git command fails."""


class AdoApiError(ReviewForgeError):
    """Raised when Azure DevOps input, API, or helper execution fails."""


class DependencyError(ReviewForgeError):
    """Raised when a required runtime tool is unavailable."""


class InputError(ReviewForgeError):
    """Raised when a caller supplies an invalid or missing input."""


class SchemaValidationError(ReviewForgeError):
    """Raised when a model response does not validate against the expected schema."""


class ProjectionError(ReviewForgeError):
    """Raised when a canonical result cannot be projected into a legacy shape."""


__all__ = [
    "ProjectionError",
    "ReasoningEngineError",
    "ReviewForgeError",
    "SchemaValidationError",
    "AdoApiError",
    "DependencyError",
    "GitOperationError",
    "InputError",
    "PiExecutionError",
    "emit_domain_error",
]
