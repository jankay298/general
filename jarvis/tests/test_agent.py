"""Tests für die Agentenschleife — mit einem gefälschten Client, ohne Netz.

Der Fake ahmt nur so viel vom SDK nach, wie die Schleife wirklich benutzt:
einen Stream-Kontextmanager, der Ereignisse liefert und am Ende eine
Nachricht mit ``stop_reason`` und ``content`` zurückgibt.
"""

from __future__ import annotations

import copy
from dataclasses import dataclass, field
from typing import Any

import pytest

from jarvis.agent import Agent
from jarvis.config import (
    AgentConfig,
    Config,
    FilesConfig,
    MailConfig,
    MarketsConfig,
    ModelConfig,
    PermissionConfig,
    ResearchConfig,
)
from jarvis.memory import Memory
from jarvis.permissions import Permissions
from jarvis.tools import ToolRegistry
from jarvis.tools.base import Tool, ToolError, obj, prop


# --------------------------------------------------------------------------- #
# Fake-SDK
# --------------------------------------------------------------------------- #


@dataclass
class FakeUsage:
    input_tokens: int = 10
    output_tokens: int = 5
    cache_read_input_tokens: int = 0
    cache_creation_input_tokens: int = 0


@dataclass
class TextBlock:
    text: str
    type: str = "text"


@dataclass
class ToolUseBlock:
    id: str
    name: str
    input: dict[str, Any]
    type: str = "tool_use"


@dataclass
class FakeMessage:
    content: list[Any]
    stop_reason: str = "end_turn"
    usage: FakeUsage = field(default_factory=FakeUsage)
    stop_details: Any = None


class FakeStream:
    def __init__(self, message: FakeMessage) -> None:
        self._message = message

    def __enter__(self) -> "FakeStream":
        return self

    def __exit__(self, *exc: object) -> None:
        return None

    def __iter__(self):
        return iter(())  # Streaming-Ereignisse interessieren hier nicht.

    def get_final_message(self) -> FakeMessage:
        return self._message


class FakeMessages:
    def __init__(self, responses: list[FakeMessage]) -> None:
        self._responses = list(responses)
        self.calls: list[dict[str, Any]] = []

    def stream(self, **kwargs: Any) -> FakeStream:
        # Momentaufnahme: der Agent reicht seine lebende Nachrichtenliste
        # durch, das echte SDK serialisiert sie sofort. Ohne Kopie würden
        # alle aufgezeichneten Aufrufe auf dieselbe, weiterwachsende Liste
        # zeigen.
        self.calls.append(copy.deepcopy(kwargs))
        if not self._responses:
            raise AssertionError("Das Modell wurde öfter aufgerufen als erwartet.")
        return FakeStream(self._responses.pop(0))


class FakeClient:
    def __init__(self, responses: list[FakeMessage]) -> None:
        self.beta = type("Beta", (), {"messages": FakeMessages(responses)})()

    @property
    def calls(self) -> list[dict[str, Any]]:
        return self.beta.messages.calls


# --------------------------------------------------------------------------- #
# Aufbau
# --------------------------------------------------------------------------- #


def make_config(tmp_path, **overrides) -> Config:
    return Config(
        path=None,
        data_dir=tmp_path,
        model=overrides.get("model", ModelConfig()),
        agent=AgentConfig(name="Jarvis"),
        permissions=PermissionConfig(),
        files=FilesConfig(enabled=False, roots=[]),
        mail=MailConfig(enabled=False),
        markets=MarketsConfig(enabled=False),
        research=overrides.get("research", ResearchConfig(enabled=False)),
        routines=[],
    )


def echo_tool(calls: list[dict[str, Any]]) -> Tool:
    def handler(_ctx, args):
        calls.append(args)
        return f"echo: {args.get('value')}"

    return Tool(
        name="echo",
        description="Gibt den Eingabewert zurück.",
        input_schema=obj({"value": prop("string", "Text")}, ["value"]),
        risk="write",
        handler=handler,
    )


def exploding_tool() -> Tool:
    def handler(_ctx, _args):
        raise ToolError("Die Datei ist verschwunden.")

    return Tool(
        name="boom",
        description="Scheitert immer.",
        input_schema=obj({}),
        risk="read",
        handler=handler,
    )


@pytest.fixture
def memory(tmp_path):
    with Memory(tmp_path / "test.db") as mem:
        yield mem


def build_agent(tmp_path, memory, responses, *, tools=None, permissions=None):
    config = make_config(tmp_path)
    registry = ToolRegistry(tools or [])
    perms = permissions or Permissions(default_read="allow", default_write="allow")
    client = FakeClient(responses)
    return Agent(config, memory, perms, registry, client=client, session="test"), client


# --------------------------------------------------------------------------- #
# Tests
# --------------------------------------------------------------------------- #


def test_plain_answer_comes_back(tmp_path, memory):
    agent, _ = build_agent(tmp_path, memory, [FakeMessage([TextBlock("Guten Morgen.")])])
    turn = agent.run("Hallo")
    assert turn.text == "Guten Morgen."
    assert turn.tool_calls == 0
    assert turn.usage.input_tokens == 10


def test_tool_call_is_executed_and_the_result_fed_back(tmp_path, memory):
    calls: list[dict[str, Any]] = []
    responses = [
        FakeMessage([ToolUseBlock("tu_1", "echo", {"value": "hallo"})], stop_reason="tool_use"),
        FakeMessage([TextBlock("Fertig.")]),
    ]
    agent, client = build_agent(tmp_path, memory, responses, tools=[echo_tool(calls)])

    turn = agent.run("Sag hallo")

    assert calls == [{"value": "hallo"}]
    assert turn.tool_calls == 1
    assert turn.text == "Fertig."

    # Die zweite Anfrage muss das Werkzeugergebnis enthalten.
    second = client.calls[1]["messages"][-1]
    assert second["role"] == "user"
    assert second["content"][0]["tool_use_id"] == "tu_1"
    assert "echo: hallo" in second["content"][0]["content"]


def test_denied_tool_reports_back_as_an_error_instead_of_running(tmp_path, memory):
    calls: list[dict[str, Any]] = []
    responses = [
        FakeMessage([ToolUseBlock("tu_1", "echo", {"value": "x"})], stop_reason="tool_use"),
        FakeMessage([TextBlock("Verstanden.")]),
    ]
    perms = Permissions(default_write="deny")
    agent, client = build_agent(
        tmp_path, memory, responses, tools=[echo_tool(calls)], permissions=perms
    )

    agent.run("Mach was")

    assert calls == []  # Der Handler wurde nie aufgerufen.
    result = client.calls[1]["messages"][-1]["content"][0]
    assert result["is_error"] is True
    assert "gesperrt" in result["content"]


def test_tool_error_is_returned_to_the_model_not_raised(tmp_path, memory):
    responses = [
        FakeMessage([ToolUseBlock("tu_1", "boom", {})], stop_reason="tool_use"),
        FakeMessage([TextBlock("Dann eben anders.")]),
    ]
    agent, client = build_agent(tmp_path, memory, responses, tools=[exploding_tool()])

    turn = agent.run("Los")

    result = client.calls[1]["messages"][-1]["content"][0]
    assert result["is_error"] is True
    assert "verschwunden" in result["content"]
    assert turn.text == "Dann eben anders."


def test_unknown_tool_does_not_crash(tmp_path, memory):
    responses = [
        FakeMessage([ToolUseBlock("tu_1", "gibtsnicht", {})], stop_reason="tool_use"),
        FakeMessage([TextBlock("Ok.")]),
    ]
    agent, client = build_agent(tmp_path, memory, responses)

    agent.run("Los")

    result = client.calls[1]["messages"][-1]["content"][0]
    assert result["is_error"] is True
    assert "gibt es nicht" in result["content"]


def test_pause_turn_resumes_without_a_tool_result(tmp_path, memory):
    """Serverseitige Werkzeuge pausieren — die Schleife muss weiterlaufen."""
    responses = [
        FakeMessage([TextBlock("Ich suche...")], stop_reason="pause_turn"),
        FakeMessage([TextBlock("Gefunden.")]),
    ]
    agent, client = build_agent(tmp_path, memory, responses)

    turn = agent.run("Recherchiere")

    assert len(client.calls) == 2
    # Die Fortsetzung schickt keine Nutzernachricht hinterher.
    assert client.calls[1]["messages"][-1]["role"] == "assistant"
    assert "Gefunden." in turn.text


def test_refusal_is_reported_and_stops_the_loop(tmp_path, memory):
    responses = [FakeMessage([], stop_reason="refusal")]
    agent, client = build_agent(tmp_path, memory, responses)

    turn = agent.run("Etwas Verbotenes")

    assert turn.stopped_because == "refusal"
    assert "Sicherheitsfilter" in turn.text
    assert len(client.calls) == 1


def test_tool_rounds_are_capped(tmp_path, memory):
    """Eine Endlosschleife aus Werkzeugaufrufen darf nicht ewig laufen."""
    calls: list[dict[str, Any]] = []
    config_rounds = 3
    responses = [
        FakeMessage([ToolUseBlock(f"tu_{i}", "echo", {"value": str(i)})], stop_reason="tool_use")
        for i in range(config_rounds)
    ]
    agent, client = build_agent(tmp_path, memory, responses, tools=[echo_tool(calls)])
    agent.config.model.max_tool_rounds = config_rounds

    turn = agent.run("Dreh dich im Kreis")

    assert turn.stopped_because == "max_rounds"
    assert len(client.calls) == config_rounds


def test_request_carries_the_expected_model_parameters(tmp_path, memory):
    agent, client = build_agent(tmp_path, memory, [FakeMessage([TextBlock("ok")])])
    agent.run("Hallo")

    request = client.calls[0]
    assert request["model"] == "claude-opus-5"
    assert request["output_config"] == {"effort": "high"}
    assert request["fallbacks"] == "default"
    # Opus 5 lehnt diese Parameter mit 400 ab — sie dürfen nicht auftauchen.
    assert "temperature" not in request
    assert "top_p" not in request
    assert "top_k" not in request


def test_system_prompt_is_cached_and_memory_sits_behind_the_cache_point(tmp_path, memory):
    memory.remember("Nutzer heißt Jan", "person")
    agent, client = build_agent(tmp_path, memory, [FakeMessage([TextBlock("ok")])])
    agent.run("Hallo")

    system = client.calls[0]["system"]
    assert system[0]["cache_control"] == {"type": "ephemeral"}
    assert "Jan" in system[1]["text"]
    assert "cache_control" not in system[1]


def test_date_is_injected_once_as_a_system_message(tmp_path, memory):
    responses = [FakeMessage([TextBlock("a")]), FakeMessage([TextBlock("b")])]
    agent, client = build_agent(tmp_path, memory, responses)

    agent.run("erste Frage")
    agent.run("zweite Frage")

    roles_first = [m["role"] for m in client.calls[0]["messages"]]
    roles_second = [m["role"] for m in client.calls[1]["messages"]]
    assert roles_first == ["user", "system"]
    assert roles_second.count("system") == 1  # nicht erneut eingefügt


def test_history_is_persisted_as_text(tmp_path, memory):
    agent, _ = build_agent(tmp_path, memory, [FakeMessage([TextBlock("Antwort.")])])
    agent.run("Frage?")

    turns = memory.load_turns("test")
    assert turns == [
        {"role": "user", "content": "Frage?"},
        {"role": "assistant", "content": "Antwort."},
    ]


def test_journal_records_every_tool_call(tmp_path, memory):
    calls: list[dict[str, Any]] = []
    responses = [
        FakeMessage([ToolUseBlock("tu_1", "echo", {"value": "x"})], stop_reason="tool_use"),
        FakeMessage([TextBlock("ok")]),
    ]
    agent, _ = build_agent(tmp_path, memory, responses, tools=[echo_tool(calls)])
    agent.run("Los")

    entries = memory.journal()
    assert len(entries) == 1
    assert entries[0].tool == "echo"
    assert entries[0].decision == "ausgeführt"
