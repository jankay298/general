using System;
using System.Collections.Generic;

namespace Daytrading.Strategies.Model;

/// <summary>
/// Alles, was eine Strategie zum Zeitpunkt einer abgeschlossenen Bar sehen darf.
/// </summary>
/// <remarks>
/// Der Snapshot wird vom Host erzeugt, nachdem die Bar geschlossen ist. Er enthält
/// bewusst keinen Kontostand, kein Equity, keine Kosten und keine zukünftigen Bars.
/// Look-ahead-Bias ist damit keine Frage der Disziplin, sondern der API-Oberfläche.
/// </remarks>
public readonly struct MarketSnapshot
{
    private static readonly IReadOnlyList<Position> NoPositions = new Position[0];

    public MarketSnapshot(
        SymbolInfo symbol,
        Timeframe timeframe,
        IBarSeries history,
        TradingSession session,
        IReadOnlyList<Position>? openPositions = null)
    {
        if (symbol == null)
        {
            throw new ArgumentNullException(nameof(symbol));
        }

        if (history == null)
        {
            throw new ArgumentNullException(nameof(history));
        }

        if (history.Count == 0)
        {
            throw new ArgumentException(
                "Ein Snapshot braucht mindestens eine abgeschlossene Bar.", nameof(history));
        }

        Symbol = symbol;
        Timeframe = timeframe;
        History = history;
        Session = session;
        OpenPositions = openPositions ?? NoPositions;
    }

    public SymbolInfo Symbol { get; }

    public Timeframe Timeframe { get; }

    /// <summary>Historie bis einschließlich der aktuellen Bar - und keine Bar weiter.</summary>
    public IBarSeries History { get; }

    /// <summary>Handelstag und Sessiongrenzen in UTC.</summary>
    public TradingSession Session { get; }

    /// <summary>Offene Positionen dieser Strategie auf diesem Symbol.</summary>
    public IReadOnlyList<Position> OpenPositions { get; }

    /// <summary>Die gerade abgeschlossene Bar.</summary>
    public Candle Current => History.Last(0);

    /// <summary>Beginn der aktuellen Bar in UTC.</summary>
    public DateTime BarOpenTimeUtc => Current.OpenTimeUtc;

    /// <summary>
    /// Ende der aktuellen Bar in UTC - der Zeitpunkt, zu dem die Strategie tatsächlich
    /// entscheidet. Alle Zeitvergleiche gegen die Session laufen über diesen Wert.
    /// </summary>
    public DateTime BarCloseTimeUtc => Current.OpenTimeUtc + Timeframe.Duration;

    /// <summary>Ob zum Barende noch Session läuft.</summary>
    public bool IsInSession => Session.Contains(BarCloseTimeUtc);

    /// <summary>Zeit seit Sessionbeginn zum Barende.</summary>
    public TimeSpan TimeSinceSessionStart => Session.Elapsed(BarCloseTimeUtc);

    /// <summary>Restzeit bis Sessionende zum Barende.</summary>
    public TimeSpan TimeUntilSessionEnd => Session.Remaining(BarCloseTimeUtc);

    /// <summary>Ob die Strategie aktuell eine offene Position auf diesem Symbol hat.</summary>
    public bool HasOpenPosition => OpenPositions.Count > 0;
}
