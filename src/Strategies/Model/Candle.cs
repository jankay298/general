using System;
using System.Globalization;

namespace Daytrading.Strategies.Model;

/// <summary>
/// Eine abgeschlossene Kursbar. Neutraler Datentyp ohne jede Broker- oder cAlgo-Bindung.
/// </summary>
/// <remarks>
/// Zwei Invarianten werden im Konstruktor erzwungen, weil sie sonst still durch die
/// gesamte Pipeline rutschen:
/// <list type="bullet">
///   <item>Der Zeitstempel ist UTC. Zeitzonennormalisierung passiert in der Datenschicht,
///         danach existiert intern nur noch UTC.</item>
///   <item>Die OHLC-Werte sind konsistent (High ist das Maximum, Low das Minimum).
///         Kaputte Bars sollen beim Einlesen einen klaren Fehler erzeugen und dort im
///         Datenqualitätsreport landen - nicht Wochen später als unerklärliches Backtest-Ergebnis.</item>
/// </list>
/// Preise sind <see cref="decimal"/>: exakte Tick-Rundung und exakte P&amp;L-Summen sind
/// hier mehr wert als die Rechengeschwindigkeit von <see cref="double"/>.
/// </remarks>
public readonly struct Candle : IEquatable<Candle>
{
    public Candle(DateTime openTimeUtc, decimal open, decimal high, decimal low, decimal close, decimal volume)
    {
        if (openTimeUtc.Kind != DateTimeKind.Utc)
        {
            throw new ArgumentException(
                $"Bar-Zeitstempel muss UTC sein, war aber '{openTimeUtc.Kind}' ({Format(openTimeUtc)}). " +
                "Die Datenschicht normalisiert alle Quellen auf UTC.",
                nameof(openTimeUtc));
        }

        if (high < low)
        {
            throw new ArgumentException(
                $"Fehlerhafte Bar {Format(openTimeUtc)}: High {high} liegt unter Low {low}.",
                nameof(high));
        }

        if (high < open || high < close)
        {
            throw new ArgumentException(
                $"Fehlerhafte Bar {Format(openTimeUtc)}: High {high} ist kleiner als Open {open} bzw. Close {close}.",
                nameof(high));
        }

        if (low > open || low > close)
        {
            throw new ArgumentException(
                $"Fehlerhafte Bar {Format(openTimeUtc)}: Low {low} ist größer als Open {open} bzw. Close {close}.",
                nameof(low));
        }

        if (volume < 0m)
        {
            throw new ArgumentException(
                $"Fehlerhafte Bar {Format(openTimeUtc)}: negatives Volumen {volume}.",
                nameof(volume));
        }

        OpenTimeUtc = openTimeUtc;
        Open = open;
        High = high;
        Low = low;
        Close = close;
        Volume = volume;
    }

    /// <summary>Beginn der Bar in UTC. Das Ende ergibt sich aus dem Timeframe des Snapshots.</summary>
    public DateTime OpenTimeUtc { get; }

    public decimal Open { get; }

    public decimal High { get; }

    public decimal Low { get; }

    public decimal Close { get; }

    /// <summary>Volumen der Bar. Je nach Quelle Stück, Kontrakte oder Ticks - die Quelle steht im Manifest.</summary>
    public decimal Volume { get; }

    /// <summary>Spanne der Bar (High - Low). Nie negativ.</summary>
    public decimal Range => High - Low;

    /// <summary>Körper der Bar (Close - Open), vorzeichenbehaftet.</summary>
    public decimal Body => Close - Open;

    public bool IsBullish => Close > Open;

    public bool IsBearish => Close < Open;

    /// <summary>Typischer Preis (H + L + C) / 3.</summary>
    public decimal TypicalPrice => (High + Low + Close) / 3m;

    public bool Equals(Candle other) =>
        OpenTimeUtc == other.OpenTimeUtc
        && Open == other.Open
        && High == other.High
        && Low == other.Low
        && Close == other.Close
        && Volume == other.Volume;

    public override bool Equals(object? obj) => obj is Candle other && Equals(other);

    public override int GetHashCode()
    {
        unchecked
        {
            var hash = OpenTimeUtc.GetHashCode();
            hash = (hash * 397) ^ Open.GetHashCode();
            hash = (hash * 397) ^ High.GetHashCode();
            hash = (hash * 397) ^ Low.GetHashCode();
            hash = (hash * 397) ^ Close.GetHashCode();
            hash = (hash * 397) ^ Volume.GetHashCode();
            return hash;
        }
    }

    public static bool operator ==(Candle left, Candle right) => left.Equals(right);

    public static bool operator !=(Candle left, Candle right) => !left.Equals(right);

    public override string ToString() =>
        string.Format(
            CultureInfo.InvariantCulture,
            "{0} O={1} H={2} L={3} C={4} V={5}",
            Format(OpenTimeUtc), Open, High, Low, Close, Volume);

    private static string Format(DateTime value) =>
        value.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
}
