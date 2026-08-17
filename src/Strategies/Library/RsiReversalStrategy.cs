using System;
using Daytrading.Strategies.Indicators;
using Daytrading.Strategies.Model;

namespace Daytrading.Strategies.Library;

/// <summary>
/// Gegenbewegung nach einem Extremwert des RSI, optional nur in Richtung des übergeordneten Trends.
/// </summary>
/// <remarks>
/// Einstieg nicht am Extrem selbst, sondern beim <b>Verlassen</b> der Zone: Der RSI muss unter
/// die Schwelle gefallen und wieder darüber gestiegen sein. Ein Markt, der überverkauft ist,
/// kann beliebig lange überverkauft bleiben - wer beim Unterschreiten kauft, steht in jedem
/// starken Abwärtstag auf der falschen Seite.
///
/// Der Trendfilter ist abschaltbar, weil beide Lesarten vertretbar sind: Gegen den Trend zu
/// handeln ist die reine Rückkehr-zum-Mittelwert-Idee, mit dem Trend zu handeln macht daraus
/// einen Rücksetzer-Einstieg. Welche der beiden trägt, ist eine Messfrage.
/// </remarks>
public sealed class RsiReversalStrategy : IStrategy
{
    private RelativeStrengthIndex _rsi = new RelativeStrengthIndex(14);
    private AverageTrueRange _atr = new AverageTrueRange(14);
    private ExponentialMovingAverage _trend = new ExponentialMovingAverage(100);

    private decimal _oversold;
    private decimal _overbought;
    private decimal _stopAtrMultiple;
    private decimal _takeProfitR;
    private bool _tradeWithTrend;
    private int _maxEntriesPerDay;
    private decimal? _riskPercent;

    private bool _wasOversold;
    private bool _wasOverbought;
    private DateTime _day = DateTime.MinValue;
    private int _entriesToday;

    public StrategyDescriptor Descriptor { get; } = new StrategyDescriptor("RsiReversal", "1.0.0");

    public int WarmupBars => 120;

    public void Initialize(StrategyContext context)
    {
        var parameters = context.Parameters;
        _oversold = parameters.GetDecimal("Oversold", 30m, min: 1m, max: 49m);
        _overbought = parameters.GetDecimal("Overbought", 70m, min: 51m, max: 99m);
        _stopAtrMultiple = parameters.GetDecimal("StopAtrMultiple", 1.5m, min: 0.2m, max: 10m);
        _takeProfitR = parameters.GetDecimal("TakeProfitR", 2m, min: 0m, max: 20m);
        _tradeWithTrend = parameters.GetBool("TradeWithTrend", false);
        _maxEntriesPerDay = parameters.GetInt("MaxEntriesPerDay", 2, min: 1, max: 20);

        var rsiPeriod = parameters.GetInt("RsiPeriod", 14, min: 2, max: 100);
        var atrPeriod = parameters.GetInt("AtrPeriod", 14, min: 2, max: 200);
        var trendPeriod = parameters.GetInt("TrendMaPeriod", 100, min: 5, max: 500);

        var risk = parameters.GetDecimal("RiskPercent", 0m, min: 0m, max: 100m);
        _riskPercent = risk > 0m ? risk : (decimal?)null;

        _rsi = new RelativeStrengthIndex(rsiPeriod);
        _atr = new AverageTrueRange(atrPeriod);
        _trend = new ExponentialMovingAverage(trendPeriod);
    }

    public Signal? OnBar(MarketSnapshot snapshot)
    {
        var bar = snapshot.Current;

        if (snapshot.BarCloseTimeUtc.Date != _day)
        {
            _day = snapshot.BarCloseTimeUtc.Date;
            _entriesToday = 0;
        }

        _rsi.Update(bar.Close);
        _atr.Update(bar);
        _trend.Update(bar.Close);

        if (!_rsi.IsReady || !_atr.IsReady || !_trend.IsReady)
        {
            return null;
        }

        var value = _rsi.Value;
        var leftOversold = _wasOversold && value > _oversold;
        var leftOverbought = _wasOverbought && value < _overbought;

        _wasOversold = value <= _oversold;
        _wasOverbought = value >= _overbought;

        if (snapshot.HasOpenPosition || _entriesToday >= _maxEntriesPerDay)
        {
            return null;
        }

        var stopDistance = _atr.Value * _stopAtrMultiple;
        if (stopDistance <= 0m)
        {
            return null;
        }

        var trendUp = bar.Close > _trend.Value;

        if (leftOversold && (!_tradeWithTrend || trendUp))
        {
            _entriesToday++;
            return Entry(TradeDirection.Long, bar.Close, stopDistance, "RSI verlaesst die untere Zone");
        }

        if (leftOverbought && (!_tradeWithTrend || !trendUp))
        {
            _entriesToday++;
            return Entry(TradeDirection.Short, bar.Close, stopDistance, "RSI verlaesst die obere Zone");
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
