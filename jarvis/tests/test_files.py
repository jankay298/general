import pytest

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
from jarvis.tools.base import ToolContext, ToolError
from jarvis.tools.files import (
    _safe_path,
    directory_report,
    find_duplicates,
    list_directory,
    move_file,
    organize_directory,
)


@pytest.fixture
def ctx(tmp_path):
    """Kontext mit genau einem freigegebenen Ordner, ohne Konfigurationsdatei."""
    root = tmp_path / "workspace"
    root.mkdir()
    cfg = Config(
        path=None,
        data_dir=tmp_path / "data",
        model=ModelConfig(),
        agent=AgentConfig(),
        permissions=PermissionConfig(),
        files=FilesConfig(enabled=True, roots=[root]),
        mail=MailConfig(),
        markets=MarketsConfig(),
        research=ResearchConfig(),
        routines=[],
    )
    return ToolContext(config=cfg, memory=None), root  # type: ignore[arg-type]


# --------------------------------------------------------------------------- #
# Pfadsicherheit — der wichtigste Test der ganzen Datei
# --------------------------------------------------------------------------- #


def test_path_inside_root_is_accepted(ctx):
    context, root = ctx
    (root / "datei.txt").write_text("hallo", encoding="utf-8")
    assert _safe_path(context, str(root / "datei.txt")).name == "datei.txt"


def test_path_outside_root_is_rejected(ctx):
    context, root = ctx
    with pytest.raises(ToolError, match="außerhalb"):
        _safe_path(context, "/etc/passwd")


def test_traversal_out_of_root_is_rejected(ctx):
    context, root = ctx
    with pytest.raises(ToolError, match="außerhalb"):
        _safe_path(context, str(root / ".." / ".." / "etc" / "passwd"))


def test_symlink_pointing_out_of_root_is_rejected(ctx, tmp_path):
    context, root = ctx
    secret = tmp_path / "geheim.txt"
    secret.write_text("nicht für dich", encoding="utf-8")
    link = root / "link.txt"
    link.symlink_to(secret)
    with pytest.raises(ToolError, match="außerhalb"):
        _safe_path(context, str(link))


def test_no_roots_configured_gives_a_useful_message(ctx):
    context, _root = ctx
    context.config.files.roots = []
    with pytest.raises(ToolError, match="kein Arbeitsverzeichnis"):
        _safe_path(context, "/tmp")


# --------------------------------------------------------------------------- #
# Verhalten
# --------------------------------------------------------------------------- #


def test_list_directory_shows_entries(ctx):
    context, root = ctx
    (root / "a.txt").write_text("x", encoding="utf-8")
    (root / "b.pdf").write_text("y", encoding="utf-8")
    output = list_directory(context, {"path": str(root)})
    assert "a.txt" in output and "b.pdf" in output


def test_list_directory_respects_the_pattern(ctx):
    context, root = ctx
    (root / "a.txt").write_text("x", encoding="utf-8")
    (root / "b.pdf").write_text("y", encoding="utf-8")
    output = list_directory(context, {"path": str(root), "pattern": "*.pdf"})
    assert "b.pdf" in output and "a.txt" not in output


def test_organize_dry_run_changes_nothing(ctx):
    context, root = ctx
    (root / "foto.jpg").write_text("x", encoding="utf-8")
    (root / "brief.pdf").write_text("y", encoding="utf-8")

    output = organize_directory(context, {"path": str(root)})

    assert "Vorschau" in output
    assert (root / "foto.jpg").exists()
    assert not (root / "Bilder").exists()


def test_organize_moves_files_into_categories(ctx):
    context, root = ctx
    (root / "foto.jpg").write_text("x", encoding="utf-8")
    (root / "brief.pdf").write_text("y", encoding="utf-8")
    (root / "seltsam.xyz").write_text("z", encoding="utf-8")

    organize_directory(context, {"path": str(root), "dry_run": False})

    assert (root / "Bilder" / "foto.jpg").exists()
    assert (root / "Dokumente" / "brief.pdf").exists()
    assert (root / "Sonstiges" / "seltsam.xyz").exists()


def test_organize_never_overwrites(ctx):
    context, root = ctx
    (root / "Bilder").mkdir()
    (root / "Bilder" / "foto.jpg").write_text("alt", encoding="utf-8")
    (root / "foto.jpg").write_text("neu", encoding="utf-8")

    organize_directory(context, {"path": str(root), "dry_run": False})

    assert (root / "Bilder" / "foto.jpg").read_text(encoding="utf-8") == "alt"
    assert (root / "Bilder" / "foto (2).jpg").read_text(encoding="utf-8") == "neu"


def test_organize_ignores_hidden_files_and_folders(ctx):
    context, root = ctx
    (root / ".versteckt").write_text("x", encoding="utf-8")
    (root / "unterordner").mkdir()

    output = organize_directory(context, {"path": str(root)})

    assert "nichts einzusortieren" in output


def test_move_file_refuses_to_leave_the_root(ctx):
    context, root = ctx
    (root / "a.txt").write_text("x", encoding="utf-8")
    with pytest.raises(ToolError, match="außerhalb"):
        move_file(context, {"source": str(root / "a.txt"), "destination": "/tmp/a.txt"})


def test_find_duplicates_reports_identical_content(ctx):
    context, root = ctx
    (root / "eins.txt").write_text("gleicher inhalt", encoding="utf-8")
    (root / "zwei.txt").write_text("gleicher inhalt", encoding="utf-8")
    (root / "drei.txt").write_text("anderer inhalt!", encoding="utf-8")

    output = find_duplicates(context, {"path": str(root)})

    assert "eins.txt" in output and "zwei.txt" in output
    assert "drei.txt" not in output


def test_directory_report_groups_by_category(ctx):
    context, root = ctx
    (root / "a.jpg").write_text("x" * 100, encoding="utf-8")
    (root / "b.pdf").write_text("y" * 50, encoding="utf-8")

    output = directory_report(context, {"path": str(root)})

    assert "Bilder: 1" in output
    assert "Dokumente: 1" in output
