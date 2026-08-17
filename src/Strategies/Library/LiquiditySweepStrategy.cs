using System;
using Daytrading.Strategies.Indicators;
using Daytrading.Strategies.Model;

namespace Daytrading.Strategies.Library;

/// <summary>
/// Steigt ein, nachdem ein Kursdocht ein vorheriges Tief oder Hoch abgeräumt hat und der
/// Schlusskurs sofort wieder zurückkommt.
/// </summary>
/// <remarks>
/// Die von Hand gehandelte Regel: In einem insgesamt aufwärts gerichteten Markt wird nicht
/// irgendein Rücksetzer gekauft, sondern der Moment, in dem der Kurs kurz unter ein sichtbares
/// Tief taucht - dorthin, wo die Stops liegen - und gleich wieder darüber schließt.
///
/// Der Gedanke dahinter ist eine Aussage über <b>andere Marktteilnehmer</b>, nicht über eine
/// Kurve: Unter einem auffälligen Tief liegen Verkaufsstops. Werden sie ausgelöst und der Kurs
/// kommt dennoch sofort zurück, hat der Verkaufsdruck genau dort geendet, wo er am größten
/// hätte sein müssen. Das ist der Grund, warum diese Regel keine Variante der übrigen ist: Sie
/// stützt sich nicht auf eine Glättung des Kurses, sondern auf eine Stelle, an der bekanntlich
/// Aufträge liegen.
///
/// Drei Bedingungen, alle beim Schluss der Signalbar bekannt:
/// <list type="number">
///   <item>Ein Extrem der letzten <c>SwingLookback</c> Bars, die laufende ausgenommen.</item>
///   <item>Die laufende Bar durchsticht es mit Docht - <c>Low</c> darunter, <c>Close</c>
///         wieder darüber. Ein Schluss jenseits wäre ein Bruch, kein Abräumen.</item>
///   <item>Optionaler Trendfilter: gekauft wird nur oberhalb des langen Durchschnitts.
///         Abschaltbar, weil beide Lesarten vertretbar sind und die Messung entscheiden soll.</item>
/// </list>
///
/// Der Stop sitzt unter dem Docht: Kommt der Kurs dorthin zurück, war das Abräumen keines.
/// </remarks>
public sealed class LiquiditySweepStrategy : IStrategy
{
    private RollingWindow<Candle> _window = new RollingWindow<Candle>(20);
    private AverageTrueRange _atr = new AverageTrueRange(14);
    private ExponentialMovingAverage _trend = new ExponentialMovingAverage(100);

    private int _lookback;
    private decimal _minWickAtr;
    private decimal _stopBufferAtr;
    private decimal _takeProfitR;
    private bool _useTrendFilter;
    private int _maxEntriesPerDay;
    private decimal? _riskPercent;

    private DateTime _day = DateTime.MinValue;
    private int _entriesToday;

    public StrategyDescriptor Descriptor { get; } = new StrategyDescriptor("LiquiditySweep", "1.0.0");

    public int WarmupBars => 120;

    public void Initialize(StrategyContext context)
    {
        var parameters = context.Parameters;
        _lookback = parameters.GetInt("SwingLookback", 20, min: 3, max: 200);
        _minWickAtr = parameters.GetDecimal("MinWickAtr", 0.1m, min: 0m, max: 5m);
        _stopBufferAtr = parameters.GetDecimal("StopBufferAtr", 0.25m, min: 0m, max: 5m);
        _takeProfitR = parameters.GetDecimal("TakeProfitR", 2m, min: 0m, max: 20m);
        _useTrendFilter = parameters.GetBool("UseTrendFilter", true);
        _maxEntriesPerDay = parameters.GetInt("MaxEntriesPerDay", 2, min: 1, max: 20);

        var atrPeriod = parameters.GetInt("AtrPeriod", 14, min: 2, max: 200);
        var trendPeriod = parameters.GetInt("TrendMaPeriod", 100, min: 5, max: 500);
        var risk = parameters.GetDecimal("RiskPercent", 0m, min: 0m, max: 100m);
        _riskPercent = risk > 0m ? risk : (decimal?)null;

        _window = new RollingWindow<Candle>(_lookback);
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

        var ready = _window.IsFull && _atr.IsReady && _trend.IsReady;

        // Extrem der vorherigen Bars - ohne die laufende, sonst kann sie es nie durchstechen.
        var swingHigh = decimal.MinValue;
        var swingLow = decimal.MaxValue;
        for (var i = 0; i < _window.Count; i++)
        {
            swingHigh = Math.Max(swingHigh, _window[i].High);
            swingLow = Math.Min(swingLow, _window[i].Low);
        }

        var trend = _trend.Value;

        _window.Add(bar);
        _atr.Update(bar);
        _trend.Update(bar.Close);

        if (!ready || snapshot.HasOpenPosition || _entriesToday >= _maxEntriesPerDay)
        {
            return null;
        }

        var minWick = _atr.Value * _minWickAtr;
        var buffer = _atr.Value * _stopBufferAtr;

        // Abräumen nach unten: Docht unter das Tief, Schluss wieder darüber.
        var sweptLow = bar.Low < swingLow - minWick && bar.Close > swingLow;
        if (sweptLow && (!_useTrendFilter || bar.Close > trend))
        {
            _entriesToday++;
            return Build(TradeDirection.Long, bar.Close, bar.Low - buffer, swingLow);
        }

        // Abräumen nach oben: Docht über das Hoch, Schluss wieder darunter.
        var sweptHigh = bar.High > swingHigh + minWick && bar.Close < swingHigh;
        if (sweptHigh && (!_useTrendFilter || bar.Close < trend))
        {
            _entriesToday++;
            return Build(TradeDirection.Short, bar.Close, bar.High + buffer, swingHigh);
        }

        return null;
    }

    private Signal? Build(TradeDirection direction, decimal price, decimal stop, decimal sweptLevel)
    {
        var sign = direction.Sign();
        var stopDistance = (price - stop) * sign;
        if (stopDistance <= 0m)
        {
            return null;
        }

        var target = _takeProfitR > 0m ? price + (stopDistance * _takeProfitR * sign) : (decimal?)null;
        return Signal.Entry(
            direction, price, stop, target, _riskPercent,
            $"Liquiditaet bei {sweptLevel:0.##} abgeraeumt");
    }
}
