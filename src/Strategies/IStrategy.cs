using Daytrading.Strategies.Model;

namespace Daytrading.Strategies;

/// <summary>
/// Eine Handelsstrategie. Die einzige Stelle im Framework, an der Handelslogik steht.
/// </summary>
/// <remarks>
/// Implementierungen dürfen ausschließlich mit den neutralen Datentypen aus
/// <see cref="Model"/> arbeiten. Keine Broker-API, keine Orderausführung, keine
/// Kontogröße, keine Positionsgrößenrechnung. Dieselbe Instanz läuft dadurch
/// unverändert im Backtester und im cTrader-cBot - und ist ohne cTrader testbar.
///
/// Erwartete Lebensdauer: <see cref="Initialize"/> genau einmal, danach
/// <see cref="OnBar"/> je abgeschlossener Bar in chronologischer Reihenfolge.
/// Implementierungen müssen deterministisch sein: gleiche Barfolge, gleiche Signale.
/// </remarks>
public interface IStrategy
{
    /// <summary>Name, Version und Kurzbeschreibung - landet in Ergebnismatrix und Trade-Log.</summary>
    StrategyDescriptor Descriptor { get; }

    /// <summary>
    /// Anzahl Bars, die die Strategie zum Aufwärmen braucht (z.B. für Indikatorperioden).
    /// Wird nach <see cref="Initialize"/> abgefragt, weil die Zahl von den Parametern abhängt.
    /// Der Host ruft <see cref="OnBar"/> auch währenddessen auf, wertet Signale aber
    /// erst danach aus und weist die Aufwärmphase im Report aus.
    /// </summary>
    int WarmupBars { get; }

    /// <summary>
    /// Einmalige Initialisierung: Parameter lesen, Indikatoren anlegen, Zustand zurücksetzen.
    /// Ein erneuter Aufruf muss die Strategie in den Ausgangszustand versetzen, damit
    /// Walk-Forward-Fenster sauber getrennt sind.
    /// </summary>
    void Initialize(StrategyContext context);

    /// <summary>
    /// Wird nach jeder abgeschlossenen Bar aufgerufen und gibt ein Signal zurück - oder null,
    /// wenn nichts zu tun ist. Kein Signal ist der Normalfall.
    /// </summary>
    Signal? OnBar(MarketSnapshot snapshot);
}
