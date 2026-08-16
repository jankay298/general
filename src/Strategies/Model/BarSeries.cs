using System;
using System.Collections.Generic;

namespace Daytrading.Strategies.Model;

/// <summary>
/// Nur-Lese-Sicht auf die Kurshistorie. Genau das - und nicht mehr - bekommt eine Strategie.
/// </summary>
/// <remarks>
/// Der Schutz gegen Look-ahead-Bias ist strukturell: Der Host hängt eine Bar erst an,
/// wenn sie abgeschlossen ist, und ruft danach <c>OnBar</c>. Eine Strategie kann keine
/// Bar sehen, die noch nicht existiert, weil es keine API dafür gibt.
/// </remarks>
public interface IBarSeries
{
    /// <summary>Anzahl abgeschlossener Bars bis einschließlich der aktuellen.</summary>
    int Count { get; }

    /// <summary>Chronologischer Zugriff, 0 ist die älteste Bar.</summary>
    Candle this[int index] { get; }

    /// <summary>Rückwärtszugriff: <c>Last(0)</c> ist die aktuelle Bar, <c>Last(1)</c> die davor.</summary>
    Candle Last(int offset = 0);

    /// <summary>Wie <see cref="Last(int)"/>, aber ohne Ausnahme, wenn die Historie noch zu kurz ist.</summary>
    bool TryLast(int offset, out Candle candle);
}

/// <summary>
/// Beschreibbare Implementierung von <see cref="IBarSeries"/>. Nur der Host (Backtester,
/// cBot, Tests) hält diese Klasse; Strategien sehen ausschließlich <see cref="IBarSeries"/>.
/// </summary>
public sealed class BarSeries : IBarSeries
{
    private readonly List<Candle> _candles;

    public BarSeries()
    {
        _candles = new List<Candle>();
    }

    public BarSeries(int expectedCapacity)
    {
        if (expectedCapacity < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(expectedCapacity), expectedCapacity, "Kapazität darf nicht negativ sein.");
        }

        _candles = new List<Candle>(expectedCapacity);
    }

    public int Count => _candles.Count;

    public Candle this[int index]
    {
        get
        {
            if (index < 0 || index >= _candles.Count)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(index), index, $"Historie enthält {_candles.Count} Bars.");
            }

            return _candles[index];
        }
    }

    /// <summary>
    /// Hängt eine abgeschlossene Bar an. Erzwingt streng aufsteigende Zeitstempel:
    /// Duplikate und unsortierte Daten sollen hier auffallen und nicht als
    /// Phantom-Trade im Ergebnis landen.
    /// </summary>
    public void Append(Candle candle)
    {
        if (_candles.Count > 0)
        {
            var previous = _candles[_candles.Count - 1];
            if (candle.OpenTimeUtc == previous.OpenTimeUtc)
            {
                throw new InvalidOperationException(
                    $"Doppelter Bar-Zeitstempel {candle.OpenTimeUtc:yyyy-MM-dd HH:mm:ss} in der Historie.");
            }

            if (candle.OpenTimeUtc < previous.OpenTimeUtc)
            {
                throw new InvalidOperationException(
                    $"Bars sind nicht chronologisch: {candle.OpenTimeUtc:yyyy-MM-dd HH:mm:ss} folgt auf " +
                    $"{previous.OpenTimeUtc:yyyy-MM-dd HH:mm:ss}.");
            }
        }

        _candles.Add(candle);
    }

    public Candle Last(int offset = 0)
    {
        if (!TryLast(offset, out var candle))
        {
            throw new ArgumentOutOfRangeException(
                nameof(offset), offset, $"Historie enthält {_candles.Count} Bars, angefragt wurde Offset {offset}.");
        }

        return candle;
    }

    public bool TryLast(int offset, out Candle candle)
    {
        var index = _candles.Count - 1 - offset;
        if (offset < 0 || index < 0)
        {
            candle = default;
            return false;
        }

        candle = _candles[index];
        return true;
    }

    public void Clear() => _candles.Clear();
}
