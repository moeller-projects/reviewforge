"""Shared, redacted run logging for ReviewForge artifact directories."""
from __future__ import annotations

import logging as _logging
import os
from pathlib import Path

_LOGGER_NAME = "reviewforge"
_SECRET_NAMES = (
    "ADO_AUTH_TOKEN",
    "ADO_MCP_AUTH_TOKEN",
    "ADO_API_KEY",
    "SYSTEM_ACCESSTOKEN",
    "OPENAI_API_KEY",
)


class _DynamicStderrHandler(_logging.StreamHandler):
    def emit(self, record: _logging.LogRecord) -> None:
        self.stream = __import__("sys").stderr
        super().emit(record)


def _formatter() -> _logging.Formatter:
    return _logging.Formatter("%(asctime)s %(levelname)s %(message)s", datefmt="%Y-%m-%dT%H:%M:%S%z")


def _stderr_handler() -> _logging.Handler:
    handler = _DynamicStderrHandler()
    handler.setFormatter(_formatter())
    handler.addFilter(_RedactSecrets())
    return handler

class _RedactSecrets(_logging.Filter):
    def filter(self, record: _logging.LogRecord) -> bool:
        message = record.getMessage()
        for name in _SECRET_NAMES:
            value = os.environ.get(name)
            if value:
                message = message.replace(value, "***")
        record.msg = message
        record.args = ()
        return True


def configure(log_path: Path) -> None:
    """Route ReviewForge logs to stderr and the current run's redacted log."""
    logger = _logging.getLogger(_LOGGER_NAME)
    for handler in logger.handlers:
        handler.close()
    logger.handlers.clear()
    logger.setLevel(getattr(_logging, os.getenv("REVIEW_LOG_LEVEL", "INFO").upper(), _logging.INFO))
    logger.propagate = False
    logger.addHandler(_stderr_handler())
    file_handler = _logging.FileHandler(log_path, encoding="utf-8")
    file_handler.setFormatter(_formatter())
    file_handler.addFilter(_RedactSecrets())
    logger.addHandler(file_handler)

def get_logger() -> _logging.Logger:
    logger = _logging.getLogger(_LOGGER_NAME)
    if not logger.handlers:
        logger.setLevel(_logging.INFO)
        logger.propagate = False
        logger.addHandler(_stderr_handler())
    return logger


def info(message: str) -> None:
    get_logger().info(f"[review] {message}")


def warning(message: str) -> None:
    get_logger().warning(f"[review][WARN] {message}")


def error(message: str) -> None:
    get_logger().error(f"[review][ERROR] {message}")


__all__ = ["configure", "error", "get_logger", "info", "warning"]
