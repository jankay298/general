"""Tests für die Teile des Mail-Moduls, die ohne Server auskommen.

Das sind genau die Stellen, an denen Postfächer in der Praxis wehtun:
kodierte Kopfzeilen, HTML-only-Nachrichten und Umlaute im Suchbegriff.
"""

from email.message import EmailMessage

from jarvis.tools.mail import _attachments, _body_text, _decode, _escape_imap


def test_decode_handles_encoded_headers():
    # "Grüße" als RFC-2047-kodierte Kopfzeile, wie sie real ankommt.
    assert _decode("=?utf-8?B?R3LDvMOfZQ==?=") == "Grüße"


def test_decode_passes_plain_text_through():
    assert _decode("Rechnung 2026-114") == "Rechnung 2026-114"
    assert _decode(None) == ""


def test_escape_imap_encodes_umlauts_as_utf8():
    """imaplib würde str hier als ASCII kodieren und abstürzen."""
    assert _escape_imap("Grüße") == "Grüße".encode("utf-8")


def test_escape_imap_escapes_quotes_and_backslashes():
    assert _escape_imap('er sagte "hallo"') == b'er sagte \\"hallo\\"'
    assert _escape_imap("pfad\\datei") == b"pfad\\\\datei"


def test_body_prefers_plain_text():
    msg = EmailMessage()
    msg.set_content("Nur Text.")
    msg.add_alternative("<p>Auch als HTML</p>", subtype="html")
    assert _body_text(msg) == "Nur Text."


def test_body_falls_back_to_stripped_html():
    msg = EmailMessage()
    msg["Subject"] = "Newsletter"
    msg.set_content("<html><body><p>Hallo <b>Welt</b></p></body></html>", subtype="html")
    body = _body_text(msg)
    assert "Hallo" in body and "Welt" in body
    assert "<b>" not in body


def test_attachments_are_listed_but_not_read_as_body():
    msg = EmailMessage()
    msg.set_content("Anbei die Rechnung.")
    msg.add_attachment(b"%PDF-1.4 ...", maintype="application", subtype="pdf",
                       filename="rechnung.pdf")
    assert _attachments(msg) == ["rechnung.pdf"]
    assert _body_text(msg) == "Anbei die Rechnung."


def test_empty_message_does_not_crash():
    assert _body_text(EmailMessage()) == ""
