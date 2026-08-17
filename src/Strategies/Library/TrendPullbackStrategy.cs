using System;
using Daytrading.Strategies.Indicators;
using Daytrading.Strategies.Model;

namespace Daytrading.Strategies.Library;

/// <summary>
/// Einstieg in Trendrichtung, nachdem der Kurs an den gleitenden Durchschnitt zurückgelaufen ist.
/// </summary>
/// <remarks>
/// Der Gegenentwurf zum Ausbruch: Statt der Bewegung hinterherzulaufen, wird auf den Rücksetzer
/// gewartet. Das kostet Treffer - manche Bewegung läuft ohne Rücksetzer davon -, senkt aber den
/// Einstiegspreis und damit den Stopabstand. Bei gleichem Prozentrisiko ergibt das eine
/// größere Position und ein besseres Verhältnis, wenn die Bewegung doch kommt.
///
/// Der Trend wird über zwei Durchschnitte bestimmt, der Rücksetzer über die Berührung des
/// kurzen. Eingestiegen wird erst, wenn der Kurs wieder in Trendrichtung dreht - sonst greift
/// die Regel in jeden Abwärtsimpuls hinein, der zufällig durch den Durchschnitt läuft.
/// </remarks>
public sealed class TrendPullbackStrategy : IStrategy
{
    private ExponentialMovingAverage _fast = new ExponentialMovingAverage(20);
    private ExponentialMovingAverage _slow = new ExponentialMovingAverage(50);
    private AverageTrueRange _atr = new AverageTrueRange(14);

    private decimal _stopAtrMultiple;
    private decimal _takeProfitR;
    private int _maxEntriesPerDay;
    private decimal? _riskPercent;

    private bool _pullbackLong;
    private bool _pullbackShort;
    private DateTime _day = DateTime.MinValue;
    private int _entriesToday;
    private Candle? _previous;

    public StrategyDescriptor Descriptor { get; } = new StrategyDescriptor("TrendPullback", "1.0.0");

    public int WarmupBars => 60;

    public void Initialize(StrategyContext context)
    {
        var parameters = context.Parameters;
        _stopAtrMultiple = parameters.GetDecimal("StopAtrMultiple", 1.5m, min: 0.2m, max: 10m);
        _takeProfitR = parameters.GetDecimal("TakeProfitR", 2m, min: 0m, max: 20m);
        _maxEntriesPerDay = parameters.GetInt("MaxEntriesPerDay", 2, min: 1, max: 20);

        var fastPeriod = parameters.GetInt("FastMaPeriod", 20, min: 2, max: 200);
        var slowPeriod = parameters.GetInt("SlowMaPeriod", 50, min: 3, max: 400);
        var atrPeriod = parameters.GetInt("AtrPeriod", 14, min: 2, max: 200);

        var risk = parameters.GetDecimal("RiskPercent", 0m, min: 0m, max: 100m);
        _riskPercent = risk > 0m ? risk : (decimal?)null;

        _fast = new ExponentialMovingAverage(fastPeriod);
        _slow = new ExponentialMovingAverage(slowPeriod);
        _atr = new AverageTrueRange(atrPeriod);
    }

    public Signal? OnBar(MarketSnapshot snapshot)
    {
        var bar = snapshot.Current;

        if (snapshot.BarCloseTimeUtc.Date != _day)
        {
            _day = snapshot.BarCloseTimeUtc.Date;
            _entriesToday = 0;
            _pullbackLong = false;
            _pullbackShort = false;
        }

        var ready = _slow.IsReady && _atr.IsReady && _previous.HasValue;
        var fast = _fast.Value;
        var slow = _slow.Value;
        var previous = _previous;

        _fast.Update(bar.Close);
        _slow.Update(bar.Close);
        _atr.Update(bar);
        _previous = bar;

        if (!ready)
        {
            return null;
        }

        var trendUp = fast > slow;

        // Rücksetzer vormerken: Der Kurs hat den kurzen Durchschnitt berührt.
        if (trendUp && bar.Low <= fast)
        {
            _pullbackLong = true;
        }

        if (!trendUp && bar.High >= fast)
        {
            _pullbackShort = true;
        }

        if (snapshot.HasOpenPosition || _entriesToday >= _maxEntriesPerDay)
        {
            return null;
        }

        var stopDistance = _atr.Value * _stopAtrMultiple;
        if (stopDistance <= 0m)
        {
            return null;
        }

        // Erst die Umkehr bestaetigt den Einstieg: Schlusskurs ueber dem Hoch der Vorbar.
        if (trendUp && _pullbackLong && bar.Close > previous!.Value.High)
        {
            _pullbackLong = false;
            _entriesToday++;
            return Entry(TradeDirection.Long, bar.Close, stopDistance, "Ruecksetzer im Aufwaertstrend");
        }

        if (!trendUp && _pullbackShort && bar.Close < previous!.Value.Low)
        {
            _pullbackShort = false;
            _entriesToday++;
            return Entry(TradeDirection.Short, bar.Close, stopDistance, "Ruecksetzer im Abwaertstrend");
        }

        return null;
    }

    private Signal Entry(TradeDirection direction, decimal price, decimal stopDistance, string reason)
    {
        var sign = direction.Sign();
        var stop = price - (stopDistance * sign);
        var target = _takeProfitR > 0m ? price + (stopDistance * _takeProfitR * sign) : (decimal?)null;
        return Signal.Entry(direction, price, stop, target, _riskPercent, reason);
    }
}
