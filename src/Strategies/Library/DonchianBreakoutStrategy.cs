using System;
using Daytrading.Strategies.Indicators;
using Daytrading.Strategies.Model;

namespace Daytrading.Strategies.Library;

/// <summary>
/// Ausbruch aus dem Hoch-Tief-Kanal der letzten n Bars, nur in Richtung des Trends.
/// </summary>
/// <remarks>
/// Der klassische Trendfolger. Anders als der Ausbruch aus der Eröffnungsspanne ist er nicht
/// an eine Tageszeit gebunden und kann mehrfach am Tag auslösen.
///
/// Der Trendfilter ist der eigentliche Unterschied zu einem reinen Kanalausbruch: Gehandelt
/// wird nur nach oben, wenn der kurze über dem langen Durchschnitt liegt. Ohne ihn kauft die
/// Regel jeden Ausreißer in einer Seitwärtsphase - und Seitwärtsphasen sind der Normalzustand.
/// </remarks>
public sealed class DonchianBreakoutStrategy : IStrategy
{
    private DonchianChannel _channel = new DonchianChannel(20);
    private AverageTrueRange _atr = new AverageTrueRange(14);
    private ExponentialMovingAverage _fast = new ExponentialMovingAverage(20);
    private ExponentialMovingAverage _slow = new ExponentialMovingAverage(50);

    private int _channelPeriod;
    private decimal _stopAtrMultiple;
    private decimal _takeProfitR;
    private bool _useTrendFilter;
    private decimal? _riskPercent;
    private DateTime _day = DateTime.MinValue;
    private int _entriesToday;
    private int _maxEntriesPerDay;

    public StrategyDescriptor Descriptor { get; } = new StrategyDescriptor("DonchianBreakout", "1.0.0");

    public int WarmupBars => Math.Max(_channelPeriod, 50) + 5;

    public void Initialize(StrategyContext context)
    {
        var parameters = context.Parameters;
        _channelPeriod = parameters.GetInt("ChannelPeriod", 20, min: 5, max: 200);
        _stopAtrMultiple = parameters.GetDecimal("StopAtrMultiple", 1.5m, min: 0.2m, max: 10m);
        _takeProfitR = parameters.GetDecimal("TakeProfitR", 2m, min: 0m, max: 20m);
        _useTrendFilter = parameters.GetBool("UseTrendFilter", true);
        _maxEntriesPerDay = parameters.GetInt("MaxEntriesPerDay", 2, min: 1, max: 20);

        var atrPeriod = parameters.GetInt("AtrPeriod", 14, min: 2, max: 200);
        var fastPeriod = parameters.GetInt("FastMaPeriod", 20, min: 2, max: 200);
        var slowPeriod = parameters.GetInt("SlowMaPeriod", 50, min: 3, max: 400);

        var risk = parameters.GetDecimal("RiskPercent", 0m, min: 0m, max: 100m);
        _riskPercent = risk > 0m ? risk : (decimal?)null;

        _channel = new DonchianChannel(_channelPeriod);
        _atr = new AverageTrueRange(atrPeriod);
        _fast = new ExponentialMovingAverage(fastPeriod);
        _slow = new ExponentialMovingAverage(slowPeriod);
    }

    public Signal? OnBar(MarketSnapshot snapshot)
    {
        var bar = snapshot.Current;

        if (snapshot.BarCloseTimeUtc.Date != _day)
        {
            _day = snapshot.BarCloseTimeUtc.Date;
            _entriesToday = 0;
        }

        // Der Kanal wird erst NACH der Auswertung fortgeschrieben - sonst enthielte er die
        // Bar, deren Ausbruch gerade geprüft wird, und ein Ausbruch waere unmoeglich.
        var ready = _channel.IsReady && _atr.IsReady && _slow.IsReady;
        var upper = _channel.Upper;
        var lower = _channel.Lower;
        var trendUp = _fast.Value > _slow.Value;

        _channel.Update(bar);
        _atr.Update(bar);
        _fast.Update(bar.Close);
        _slow.Update(bar.Close);

        if (!ready || snapshot.HasOpenPosition || _entriesToday >= _maxEntriesPerDay)
        {
            return null;
        }

        var stopDistance = _atr.Value * _stopAtrMultiple;
        if (stopDistance <= 0m)
        {
            return null;
        }

        if (bar.Close > upper && (!_useTrendFilter || trendUp))
        {
            _entriesToday++;
            return Entry(TradeDirection.Long, bar.Close, stopDistance, "Kanalausbruch nach oben");
        }

        if (bar.Close < lower && (!_useTrendFilter || !trendUp))
        {
            _entriesToday++;
            return Entry(TradeDirection.Short, bar.Close, stopDistance, "Kanalausbruch nach unten");
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
