using System;
using Daytrading.Strategies.Model;

namespace Daytrading.Strategies.Indicators;

/// <summary>
/// Average True Range nach Wilder, inkrementell berechnet.
/// </summary>
/// <remarks>
/// Ablauf: Die ersten <c>period</c> True Ranges werden gemittelt (Seed), danach wird mit
/// Wilders Glättung fortgeschrieben: <c>ATR = (ATR_prev * (n-1) + TR) / n</c>.
/// Die True Range der allerersten Bar ist High - Low, weil noch kein Vorgänger-Close existiert.
///
/// Der Indikator sieht ausschließlich bereits abgeschlossene Bars - er wird vom Host je Bar
/// genau einmal aktualisiert und kann daher keinen Look-ahead erzeugen.
/// </remarks>
public sealed class AverageTrueRange
{
    private readonly int _period;
    private decimal _sum;
    private int _seedCount;
    private decimal? _previousClose;
    private decimal _value;

    public AverageTrueRange(int period)
    {
        if (period <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(period), period, "ATR-Periode muss positiv sein.");
        }

        _period = period;
    }

    public int Period => _period;

    /// <summary>True, sobald genug Bars für einen belastbaren Wert vorliegen.</summary>
    public bool IsReady => _seedCount >= _period;

    /// <summary>Aktueller ATR-Wert. Vor <see cref="IsReady"/> ist er 0.</summary>
    public decimal Value => IsReady ? _value : 0m;

    /// <summary>True Range der zuletzt verarbeiteten Bar.</summary>
    public decimal LastTrueRange { get; private set; }

    public void Update(Candle candle)
    {
        var trueRange = candle.High - candle.Low;
        if (_previousClose.HasValue)
        {
            var previousClose = _previousClose.Value;
            var highToPrevClose = Math.Abs(candle.High - previousClose);
            var lowToPrevClose = Math.Abs(candle.Low - previousClose);
            trueRange = Math.Max(trueRange, Math.Max(highToPrevClose, lowToPrevClose));
        }

        LastTrueRange = trueRange;
        _previousClose = candle.Close;

        if (_seedCount < _period)
        {
            _sum += trueRange;
            _seedCount++;
            if (_seedCount == _period)
            {
                _value = _sum / _period;
            }

            return;
        }

        _value = (_value * (_period - 1) + trueRange) / _period;
    }

    public void Reset()
    {
        _sum = 0m;
        _seedCount = 0;
        _previousClose = null;
        _value = 0m;
        LastTrueRange = 0m;
    }
}
