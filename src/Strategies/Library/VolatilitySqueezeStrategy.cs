using System;
using Daytrading.Strategies.Indicators;
using Daytrading.Strategies.Model;

namespace Daytrading.Strategies.Library;

/// <summary>
/// Ausbruch nach einer Phase ungewöhnlich ruhiger Kurse.
/// </summary>
/// <remarks>
/// Die Idee unterscheidet sich von allen anderen hier: Nicht die Richtung ist der Anlass,
/// sondern die <b>Enge</b>. Wenn die Bänder um den Durchschnitt schmal werden, war der Markt
/// ruhig; auf ruhige Phasen folgen häufiger bewegte als auf bereits bewegte.
///
/// Gehandelt wird die Richtung, in die der Kurs die Enge verlässt - ohne Meinung darüber, welche
/// das sein wird. Das ist auch der Grund, warum diese Regel keinen Trendfilter hat: Sie setzt
/// auf die Ausdehnung der Schwankung, nicht auf eine Richtung.
///
/// Der Stop kommt aus der ATR und nicht aus der Bandbreite. Nach dem Ausbruch ist die
/// Bandbreite definitionsgemäß klein, ein Stop daraus wäre unmittelbar nach dem Einstieg zu eng.
/// </remarks>
public sealed class VolatilitySqueezeStrategy : IStrategy
{
    private SimpleMovingAverage _average = new SimpleMovingAverage(20);
    private AverageTrueRange _atr = new AverageTrueRange(14);
    private RollingWindow<decimal> _widths = new RollingWindow<decimal>(50);

    private decimal _bandSigma;
    private decimal _squeezeQuantile;
    private decimal _stopAtrMultiple;
    private decimal _takeProfitR;
    private int _maxEntriesPerDay;
    private decimal? _riskPercent;

    private DateTime _day = DateTime.MinValue;
    private int _entriesToday;

    public StrategyDescriptor Descriptor { get; } = new StrategyDescriptor("VolatilitySqueeze", "1.0.0");

    public int WarmupBars => 80;

    public void Initialize(StrategyContext context)
    {
        var parameters = context.Parameters;
        _bandSigma = parameters.GetDecimal("BandSigma", 2m, min: 0.5m, max: 5m);
        _squeezeQuantile = parameters.GetDecimal("SqueezeQuantile", 0.25m, min: 0.02m, max: 0.9m);
        _stopAtrMultiple = parameters.GetDecimal("StopAtrMultiple", 1.5m, min: 0.2m, max: 10m);
        _takeProfitR = parameters.GetDecimal("TakeProfitR", 2m, min: 0m, max: 20m);
        _maxEntriesPerDay = parameters.GetInt("MaxEntriesPerDay", 2, min: 1, max: 20);

        var period = parameters.GetInt("BandPeriod", 20, min: 5, max: 200);
        var lookback = parameters.GetInt("SqueezeLookback", 50, min: 10, max: 500);
        var atrPeriod = parameters.GetInt("AtrPeriod", 14, min: 2, max: 200);

        var risk = parameters.GetDecimal("RiskPercent", 0m, min: 0m, max: 100m);
        _riskPercent = risk > 0m ? risk : (decimal?)null;

        _average = new SimpleMovingAverage(period);
        _atr = new AverageTrueRange(atrPeriod);
        _widths = new RollingWindow<decimal>(lookback);
    }

    public Signal? OnBar(MarketSnapshot snapshot)
    {
        var bar = snapshot.Current;

        if (snapshot.BarCloseTimeUtc.Date != _day)
        {
            _day = snapshot.BarCloseTimeUtc.Date;
            _entriesToday = 0;
        }

        // Die Baender der VORbar entscheiden ueber den Ausbruch dieser Bar.
        var ready = _average.IsReady && _atr.IsReady && _widths.IsFull;
        var middle = _average.Value;
        var deviation = _average.StandardDeviation();
        var upper = middle + (_bandSigma * deviation);
        var lower = middle - (_bandSigma * deviation);
        var wasSqueezed = ready && IsSqueezed(deviation);

        _average.Update(bar.Close);
        _atr.Update(bar);
        if (_average.IsReady)
        {
            _widths.Add(_average.StandardDeviation());
        }

        if (!ready || !wasSqueezed || snapshot.HasOpenPosition || _entriesToday >= _maxEntriesPerDay)
        {
            return null;
        }

        var stopDistance = _atr.Value * _stopAtrMultiple;
        if (stopDistance <= 0m)
        {
            return null;
        }

        if (bar.Close > upper)
        {
            _entriesToday++;
            return Entry(TradeDirection.Long, bar.Close, stopDistance, "Ausbruch aus der Enge nach oben");
        }

        if (bar.Close < lower)
        {
            _entriesToday++;
            return Entry(TradeDirection.Short, bar.Close, stopDistance, "Ausbruch aus der Enge nach unten");
        }

        return null;
    }

    /// <summary>Gilt die aktuelle Bandbreite als eng im Vergleich zur jüngeren Vergangenheit?</summary>
    private bool IsSqueezed(decimal deviation)
    {
        var below = 0;
        for (var i = 0; i < _widths.Count; i++)
        {
            if (_widths[i] < deviation)
            {
                below++;
            }
        }

        return (decimal)below / _widths.Count <= _squeezeQuantile;
    }

    private Signal Entry(TradeDirection direction, decimal price, decimal stopDistance, string reason)
    {
        var sign = direction.Sign();
        var stop = price - (stopDistance * sign);
        var target = _takeProfitR > 0m ? price + (stopDistance * _takeProfitR * sign) : (decimal?)null;
        return Signal.Entry(direction, price, stop, target, _riskPercent, reason);
    }
}
