"""Tests for the narrow model-runner construction seam."""
from __future__ import annotations

from types import SimpleNamespace

import pytest

from reviewforge.ai.model_runner import create_model_runner
from reviewforge.ai.runner import PiCliRunner, PiRunner
from reviewforge.config import ConfigError


def test_factory_defaults_to_pi_backend():
    runner = create_model_runner(SimpleNamespace(model_backend="pi"))
    assert isinstance(runner, PiCliRunner)


def test_factory_rejects_unknown_backend():
    with pytest.raises(ConfigError, match="MODEL_BACKEND"):
        create_model_runner(SimpleNamespace(model_backend="unsupported"))


def test_pi_runner_alias_remains_compatible():
    assert PiRunner is PiCliRunner
