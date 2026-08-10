from jarvis.permissions import Permissions


def test_risk_defaults_apply_when_no_override():
    perms = Permissions(default_read="allow", default_write="ask", default_external="deny")
    assert perms.check("list_directory", "read", "x").allowed is True
    assert perms.check("mail_send", "external", "x").allowed is False


def test_tool_override_beats_risk_default():
    perms = Permissions(default_write="deny", tools={"move_file": "allow"})
    assert perms.check("move_file", "write", "x").allowed is True
    assert perms.check("organize_directory", "write", "x").allowed is False


def test_ask_without_a_human_denies_and_explains(tmp_path):
    """Routinen laufen unbeaufsichtigt — 'ask' darf dort nicht blockieren."""
    perms = Permissions(default_write="ask", overrides_path=tmp_path / "p.json")
    verdict = perms.check("move_file", "write", "Datei verschieben")
    assert verdict.allowed is False
    assert "unbeaufsichtigt" in verdict.reason


def test_ask_consults_the_human():
    answers = iter(["yes", "no"])
    perms = Permissions(default_write="ask", ask_fn=lambda *_: next(answers))
    assert perms.check("move_file", "write", "x").allowed is True
    assert perms.check("move_file", "write", "x").allowed is False


def test_always_is_persisted_and_survives_a_restart(tmp_path):
    store = tmp_path / "permissions.local.json"
    calls = []

    def ask(name, risk, summary):
        calls.append(name)
        return "always"

    perms = Permissions(default_write="ask", overrides_path=store, ask_fn=ask)
    assert perms.check("move_file", "write", "x").allowed is True
    # Zweiter Aufruf darf nicht mehr fragen.
    assert perms.check("move_file", "write", "x").allowed is True
    assert calls == ["move_file"]

    reloaded = Permissions(default_write="ask", overrides_path=store)
    assert reloaded.policy_for("move_file", "write") == "allow"


def test_never_is_persisted(tmp_path):
    store = tmp_path / "permissions.local.json"
    perms = Permissions(default_external="ask", overrides_path=store, ask_fn=lambda *_: "never")
    assert perms.check("mail_send", "external", "x").allowed is False

    reloaded = Permissions(default_external="ask", overrides_path=store)
    assert reloaded.check("mail_send", "external", "x").allowed is False


def test_corrupt_override_file_is_ignored(tmp_path):
    store = tmp_path / "permissions.local.json"
    store.write_text("{ not json", encoding="utf-8")
    perms = Permissions(default_read="allow", overrides_path=store)
    assert perms.check("list_directory", "read", "x").allowed is True
