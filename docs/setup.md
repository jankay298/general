# Setup auf Windows, Intel-MacBook und Linux

## Was läuft wo

| Teil | Windows | macOS (Intel) | macOS (Apple Silicon) | Linux |
|---|---|---|---|---|
| `src/Strategies`, `src/Execution`, `src/Evaluation` | ✅ | ✅ | ✅ | ✅ |
| `src/Data` (Datenpipeline) | ✅ | ✅ | ✅ | ✅ |
| `src/Backtester` (Matrix, Walk-Forward) | ✅ | ✅ | ✅ | ✅ |
| `tests` (Unit-Tests) | ✅ | ✅ | ✅ | ✅ |
| `src/CBot`, `src/CBotExport` (bauen) | ✅ | ✅ | ✅ | ✅ |
| cBots **ausführen** | nur in cTrader Desktop | nur in cTrader Mac | nur in cTrader Mac | ❌ |

Alles, was wir selbst schreiben, ist reines .NET ohne native Abhängigkeiten und läuft überall.
Die einzige Plattformbindung ist der cBot — der braucht die cTrader-Anwendung als Wirt.
Die CI in `.github/workflows/ci.yml` baut und testet auf allen vier Kombinationen,
Intel-macOS ausdrücklich eingeschlossen (`macos-15-intel`).

**Nicht als Laufzeitumgebung geeignet ist das Handy.** Diese Entwicklungssitzung läuft nicht
auf dem Telefon, sondern in einem Linux-Container in der Cloud; das Telefon ist nur die
Fernbedienung. Für den Handel selbst braucht es einen Rechner mit cTrader.

## Voraussetzung: .NET SDK 8 oder neuer

`global.json` verlangt mindestens 8.0.100 und erlaubt jede neuere Hauptversion
(`rollForward: latestMajor`). Ausführbare Projekte laufen zusätzlich mit
`RollForward=LatestMajor`, damit sie auch starten, wenn nur eine neuere Runtime installiert ist.

**Windows**

```powershell
winget install Microsoft.DotNet.SDK.8
```

**macOS mit Intel-Prozessor** — wichtig ist das **x64**-Paket, nicht das ARM64-Paket:

```bash
brew install --cask dotnet-sdk          # nimmt automatisch die passende Architektur
# oder manuell: https://dotnet.microsoft.com/download/dotnet/8.0 -> macOS x64 Installer
dotnet --info                            # muss "Architecture: x64" zeigen
```

**Linux (Debian/Ubuntu)**

```bash
curl -sSL https://dot.net/v1/dotnet-install.sh | bash -s -- --channel 8.0
```

## Bauen und testen

Auf allen drei Systemen identisch:

```bash
git clone https://github.com/jankay298/general.git
cd general
dotnet test

# Kompletter Durchlauf auf synthetischen Daten, ohne Netz und ohne Broker:
dotnet run --project src/Backtester -- demo
```

Erwartet: 313 bestandene Tests, keine Warnungen. Kein Netz nötig außer für den ersten
NuGet-Restore, keine Marktdaten, keine cTrader-Installation.

## cTrader-spezifisch

- **cTrader Mac ist eine native macOS-Anwendung mit Automate/cBot-Unterstützung.** Für alles
  jenseits einfacher Algorithmen wird zusätzlich das .NET SDK gebraucht; die offizielle Hilfe
  verlinkt dafür ein **x64**-Installationspaket, was für ein Intel-MacBook passt.
- **Widersprüchliche Angaben zur Intel-Unterstützung:** Der Eintrag im Mac App Store nennt
  „Apple M1 oder neuer", während Herstellerdoku und Broker-Downloads Intel und Apple Silicon
  gleichermaßen nennen. Vor dem ersten Livebetrieb auf dem Intel-MacBook also bitte einmal die
  Version aus dem **Broker-Download** installieren und prüfen — nicht die App-Store-Variante.
- **Zielframework der cBot-Seite:** Das NuGet-Paket `cTrader.Automate` zielt auf **.NET 6** und
  **.NET Framework 4.0**. Genau deshalb ist `src/Strategies` auf `netstandard2.0` gebaut: dieselbe
  Assembly lädt in beiden cTrader-Varianten und gleichzeitig im Backtester auf .NET 8.
- **Dauerbetrieb:** Ein Laptop, der zuklappt oder in den Ruhezustand geht, ist kein Träger für
  Demo- oder Livebetrieb. Dafür entweder ein durchlaufender Rechner, ein VPS oder die
  Cloud-Instanzen von cTrader, mit denen cBots unabhängig vom eigenen Gerät weiterlaufen.
  Der Backtester dagegen läuft problemlos lokal auf dem Notebook.

## Quellen

- [cTrader Mac — Automate (Hersteller-Hilfe)](https://help.ctrader.com/ctrader-mac/automate/)
- [cTrader Mac — Einführung](https://help.ctrader.com/ctrader-mac/)
- [NuGet: cTrader.Automate (Zielframeworks)](https://www.nuget.org/packages/cTrader.Automate/)
- [cTrader Algo — Migration von .NET Framework](https://help.ctrader.com/ctrader-algo/migration-from-net-framework/)
- [GitHub Actions: Intel-macOS-Runner `macos-15-intel`](https://github.blog/changelog/2025-09-19-github-actions-macos-13-runner-image-is-closing-down/)
