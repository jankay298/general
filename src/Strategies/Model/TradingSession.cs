using System;
using System.Globalization;

namespace Daytrading.Strategies.Model;

/// <summary>
/// Der Handelstag eines Symbols, in UTC aufgelöst.
/// </summary>
/// <remarks>
/// Die Session kommt aus der Symbolkonfiguration und wird vom Host (Backtester oder cBot)
/// berechnet, nicht von der Strategie. Für Aktien und Indizes ist das die Börsensitzung,
/// für Rohstoffe eine konfigurierte Liquiditätsphase, für Krypto ein künstlicher Tag
/// (Default 00:00 bis 24:00 UTC), weil es dort kein natürliches Sessionende gibt.
///
/// Auf dieser Definition sitzen später die harten Daytrading-Regeln: Zwangsschließung
/// X Minuten vor <see cref="EndUtc"/>, kein Overnight-Halten, kein Wochenende.
/// </remarks>
public readonly struct TradingSession : IEquatable<TradingSession>
{
    public TradingSession(DateTime tradingDay, DateTime startUtc, DateTime endUtc)
    {
        if (tradingDay.Kind != DateTimeKind.Utc)
        {
            throw new ArgumentException("Handelstag muss als UTC angegeben werden.", nameof(tradingDay));
        }

        if (tradingDay != tradingDay.Date)
        {
            throw new ArgumentException(
                $"Handelstag muss ein reines Datum ohne Uhrzeit sein, war '{tradingDay:O}'.", nameof(tradingDay));
        }

        if (startUtc.Kind != DateTimeKind.Utc || endUtc.Kind != DateTimeKind.Utc)
        {
            throw new ArgumentException("Sessiongrenzen müssen als UTC angegeben werden.", nameof(startUtc));
        }

        if (endUtc <= startUtc)
        {
            throw new ArgumentException(
                $"Sessionende {Format(endUtc)} liegt nicht nach dem Sessionbeginn {Format(startUtc)}.", nameof(endUtc));
        }

        TradingDay = tradingDay;
        StartUtc = startUtc;
        EndUtc = endUtc;
    }

    /// <summary>
    /// Kalendarischer Handelstag als reines Datum. Der Schlüssel, an dem Tagesverlustgrenze,
    /// Trade-Zähler und "einmal pro Tag"-Logik hängen.
    /// </summary>
    public DateTime TradingDay { get; }

    public DateTime StartUtc { get; }

    /// <summary>Ende der Session (exklusiv).</summary>
    public DateTime EndUtc { get; }

    public TimeSpan Length => EndUtc - StartUtc;

    public bool Contains(DateTime utc) => utc >= StartUtc && utc < EndUtc;

    /// <summary>Zeit seit Sessionbeginn. Negativ vor Sessionbeginn.</summary>
    public TimeSpan Elapsed(DateTime utc) => utc - StartUtc;

    /// <summary>Restzeit bis Sessionende. Negativ nach Sessionende.</summary>
    public TimeSpan Remaining(DateTime utc) => EndUtc - utc;

    public bool Equals(TradingSession other) =>
        TradingDay == other.TradingDay && StartUtc == other.StartUtc && EndUtc == other.EndUtc;

    public override bool Equals(object? obj) => obj is TradingSession other && Equals(other);

    public override int GetHashCode()
    {
        unchecked
        {
            var hash = TradingDay.GetHashCode();
            hash = (hash * 397) ^ StartUtc.GetHashCode();
            hash = (hash * 397) ^ EndUtc.GetHashCode();
            return hash;
        }
    }

    public static bool operator ==(TradingSession left, TradingSession right) => left.Equals(right);

    public static bool operator !=(TradingSession left, TradingSession right) => !left.Equals(right);

    public override string ToString() =>
        $"{TradingDay:yyyy-MM-dd} {Format(StartUtc)}-{Format(EndUtc)} UTC";

    private static string Format(DateTime value) => value.ToString("HH:mm", CultureInfo.InvariantCulture);
}
