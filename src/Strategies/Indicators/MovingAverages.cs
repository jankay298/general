using System;
using Daytrading.Strategies.Model;

namespace Daytrading.Strategies.Indicators;

/// <summary>
/// Exponentiell gewichteter Durchschnitt.
/// </summary>
/// <remarks>
/// Der erste Wert ist der einfache Durchschnitt der ersten <c>Period</c> Werte; erst danach
/// läuft die Glättung. Ein sofortiger Start beim ersten Kurs würde die ersten Bars stark
/// verzerren, und weil eine Strategie genau dort ihre ersten Signale hätte, wäre der Backtest
/// an der empfindlichsten Stelle falsch.
/// </remarks>
public sealed class ExponentialMovingAverage
{
    private readonly int _period;
    private readonly decimal _weight;
    private decimal _seedSum;
    private int _seedCount;

    public ExponentialMovingAverage(int period)
    {
        if (period < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(period), period, "Periode muss mindestens 1 sein.");
        }

        _period = period;
        _weight = 2m / (period + 1);
    }

    public int Period => _period;

    public bool IsReady => _seedCount >= _period;

    public decimal Value { get; private set; }

    public void Update(decimal price)
    {
        if (!IsReady)
        {
            _seedSum += price;
            _seedCount++;
            Value = _seedSum / _seedCount;
            return;
        }

        Value += _weight * (price - Value);
    }

    public void Reset()
    {
        _seedSum = 0m;
        _seedCount = 0;
        Value = 0m;
    }
}

/// <summary>Einfacher gleitender Durchschnitt über ein festes Fenster.</summary>
public sealed class SimpleMovingAverage
{
    private readonly RollingWindow<decimal> _window;
    private decimal _sum;

    public SimpleMovingAverage(int period)
    {
        if (period < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(period), period, "Periode muss mindestens 1 sein.");
        }

        _window = new RollingWindow<decimal>(period);
    }

    public int Period => _window.Size;

    public bool IsReady => _window.IsFull;

    public decimal Value => _window.Count == 0 ? 0m : _sum / _window.Count;

    public void Update(decimal price)
    {
        if (_window.IsFull)
        {
            _sum -= _window[_window.Count - 1];   // aeltester Wert faellt heraus
        }

        _window.Add(price);
        _sum += price;
    }

    /// <summary>Standardabweichung im Fenster. Für Bänder um den Durchschnitt.</summary>
    public decimal StandardDeviation()
    {
        if (_window.Count < 2)
        {
            return 0m;
        }

        var mean = Value;
        var sum = 0m;
        for (var i = 0; i < _window.Count; i++)
        {
            var deviation = _window[i] - mean;
            sum += deviation * deviation;
        }

        return (decimal)Math.Sqrt((double)(sum / _window.Count));
    }
}

/// <summary>
/// Relative Strength Index nach Wilder.
/// </summary>
/// <remarks>
/// Geglättet wird wie bei Wilder, nicht als einfacher Durchschnitt - die beiden liefern
/// spürbar verschiedene Werte, und die gängigen Schwellen (30/70) beziehen sich auf Wilder.
/// </remarks>
public sealed class RelativeStrengthIndex
{
    private readonly int _period;
    private decimal _averageGain;
    private decimal _averageLoss;
    private decimal _previousClose;
    private int _samples;

    public RelativeStrengthIndex(int period)
    {
        if (period < 2)
        {
            throw new ArgumentOutOfRangeException(nameof(period), period, "Periode muss mindestens 2 sein.");
        }

        _period = period;
    }

    public bool IsReady => _samples > _period;

    /// <summary>Wert zwischen 0 und 100. Vor <see cref="IsReady"/> bedeutungslos.</summary>
    public decimal Value { get; private set; }

    public void Update(decimal close)
    {
        _samples++;

        if (_samples == 1)
        {
            _previousClose = close;
            return;
        }

        var change = close - _previousClose;
        _previousClose = close;

        var gain = change > 0m ? change : 0m;
        var loss = change < 0m ? -change : 0m;

        if (_samples <= _period + 1)
        {
            _averageGain += gain;
            _averageLoss += loss;

            if (_samples == _period + 1)
            {
                _averageGain /= _period;
                _averageLoss /= _period;
            }
        }
        else
        {
            _averageGain = ((_averageGain * (_period - 1)) + gain) / _period;
            _averageLoss = ((_averageLoss * (_period - 1)) + loss) / _period;
        }

        if (!IsReady)
        {
            return;
        }

        // Ohne einen einzigen Verlust ist der Index definitionsgemäß 100.
        Value = _averageLoss == 0m
            ? 100m
            : 100m - (100m / (1m + (_averageGain / _averageLoss)));
    }
}

/// <summary>Höchstes Hoch und tiefstes Tief der letzten n Bars, ohne die laufende.</summary>
/// <remarks>
/// Die laufende Bar bleibt bewusst außen vor. Läge sie im Fenster, wäre ein Ausbruch über das
/// Hoch der letzten n Bars nie möglich - das Hoch enthielte den Ausbruch bereits.
/// </remarks>
public sealed class DonchianChannel
{
    private readonly RollingWindow<Candle> _window;

    public DonchianChannel(int period)
    {
        if (period < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(period), period, "Periode muss mindestens 1 sein.");
        }

        _window = new RollingWindow<Candle>(period);
    }

    public bool IsReady => _window.IsFull;

    public decimal Upper { get; private set; }

    public decimal Lower { get; private set; }

    public decimal Middle => (Upper + Lower) / 2m;

    /// <summary>Nimmt eine <b>abgeschlossene</b> Bar auf, nachdem sie ausgewertet wurde.</summary>
    public void Update(Candle candle)
    {
        _window.Add(candle);

        var high = decimal.MinValue;
        var low = decimal.MaxValue;
        for (var i = 0; i < _window.Count; i++)
        {
            var bar = _window[i];
            if (bar.High > high)
            {
                high = bar.High;
            }

            if (bar.Low < low)
            {
                low = bar.Low;
            }
        }

        Upper = high;
        Lower = low;
    }
}
