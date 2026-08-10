"""Prüft, dass die gebaute Anfrage eine ist, die das SDK auch annimmt.

Ohne API-Schlüssel lässt sich kein echter Aufruf machen — aber ein falsch
benannter Parameter (``effort`` statt ``output_config``, ``budget_tokens``
statt ``thinking``) fällt hier trotzdem auf, weil die Signatur des SDK
befragt wird statt einer Erinnerung daran.
"""

from __future__ import annotations

import inspect

import anthropic
import pytest

from jarvis.agent import Agent
from jarvis.config import FALLBACK_BETA, ModelConfig
from jarvis.memory import Memory
from jarvis.permissions import Permissions
from jarvis.tools import ToolRegistry, build_registry

from .test_agent import FakeClient, FakeMessage, TextBlock, make_config


@pytest.fixture
def agent(tmp_path):
    config = make_config(tmp_path)
    with Memory(tmp_path / "t.db") as memory:
        yield Agent(
            config,
            memory,
            Permissions(),
            ToolRegistry([]),
            client=FakeClient([FakeMessage([TextBlock("ok")])]),
        )


def test_every_parameter_exists_on_the_sdk_method(agent):
    kwargs = agent._request_kwargs()
    signature = inspect.signature(anthropic.Anthropic(api_key="x").beta.messages.stream)
    unknown = set(kwargs) - set(signature.parameters)
    assert not unknown, f"Das SDK kennt diese Parameter nicht: {sorted(unknown)}"


def test_forbidden_parameters_are_absent(agent):
    """Opus 5 antwortet auf diese Felder mit 400."""
    kwargs = agent._request_kwargs()
    for forbidden in ("temperature", "top_p", "top_k"):
        assert forbidden not in kwargs
    assert "budget_tokens" not in str(kwargs.get("thinking", ""))


def test_fallback_beta_flag_travels_with_the_fallback_parameter(agent):
    kwargs = agent._request_kwargs()
    assert kwargs["fallbacks"] == "default"
    assert FALLBACK_BETA in kwargs["betas"]


def test_fallbacks_can_be_switched_off(tmp_path):
    config = make_config(tmp_path, model=ModelConfig(fallbacks=False))
    with Memory(tmp_path / "t.db") as memory:
        agent = Agent(config, memory, Permissions(), ToolRegistry([]), client=FakeClient([]))
        kwargs = agent._request_kwargs()
    assert "fallbacks" not in kwargs
    assert "betas" not in kwargs


def test_show_thinking_asks_for_a_summary(tmp_path):
    config = make_config(tmp_path, model=ModelConfig(show_thinking=True))
    with Memory(tmp_path / "t.db") as memory:
        agent = Agent(config, memory, Permissions(), ToolRegistry([]), client=FakeClient([]))
        kwargs = agent._request_kwargs()
    # Der Standard ist "omitted" — ohne diese Zeile käme leerer Text zurück.
    assert kwargs["thinking"] == {"type": "adaptive", "display": "summarized"}


def test_tool_order_is_stable(tmp_path):
    """Werkzeugdefinitionen stehen vor dem Cache-Punkt.

    Wechselt ihre Reihenfolge zwischen zwei Aufrufen, ist der Prompt-Cache
    bei jeder Anfrage wertlos.
    """
    config = make_config(tmp_path)
    config.markets.enabled = True
    first = [t["name"] for t in build_registry(config).api_definitions()]
    second = [t["name"] for t in build_registry(config).api_definitions()]
    assert first == second == sorted(first)


def test_server_tools_are_declared_when_research_is_on(tmp_path):
    from jarvis.config import ResearchConfig

    config = make_config(tmp_path, research=ResearchConfig(enabled=True, max_searches=3))
    with Memory(tmp_path / "t.db") as memory:
        agent = Agent(config, memory, Permissions(), ToolRegistry([]), client=FakeClient([]))
        tools = agent._request_kwargs()["tools"]
    types = {t.get("type") for t in tools}
    assert "web_search_20260209" in types
    assert "web_fetch_20260209" in types
