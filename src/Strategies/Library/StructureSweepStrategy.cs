using System;
using Daytrading.Strategies.Indicators;
using Daytrading.Strategies.Model;

namespace Daytrading.Strategies.Library;

/// <summary>
/// Abräumen von Liquidität an einem <b>strukturellen</b> Wendepunkt, nicht an irgendeinem Docht.
/// </summary>
/// <remarks>
/// Die Verschärfung gegenüber <see cref="LiquiditySweepStrategy"/>, und der Grund für eine
/// eigene Klasse: Dort war das Ziel das tiefste Tief der letzten n Bars - das ist häufig ein
/// einzelner Ausschlag mitten in einer Bewegung, an dem niemand eine Order liegen hat. Hier ist
/// es ein bestätigter Wendepunkt der Wellenbewegung: eine Stelle, an der der Markt sichtbar
/// gedreht hat.
///
/// Nur dort ergibt die Begründung überhaupt Sinn. Die Vermutung lautet ja nicht "unter Tiefs
/// geht es hoch", sondern "unter <i>auffälligen</i> Tiefs liegen die Stops derer, die dort
/// gekauft haben". Ein Niveau, das im Chart niemandem auffällt, hat diese Eigenschaft nicht.
///
/// Über <c>MinTouches</c> lässt sich zusätzlich verlangen, dass das Niveau mehrfach angelaufen
/// wurde. Ein Doppeltief ist auffälliger als ein einzelnes und damit die bessere Vermutung
/// darüber, wo sich Aufträge sammeln.
///
/// Beide Fassungen bleiben nebeneinander bestehen, damit die Frage messbar ist, ob die
/// strukturelle Auswahl den Unterschied macht - sie ist der eigentliche Gegenstand dieser Klasse.
/// </remarks>
public sealed class StructureSweepStrategy : IStrategy
{
    private SwingStructure _structure = new SwingStructure();
    private AverageTrueRange _atr = new AverageTrueRange(14);
    private ExponentialMovingAverage _trend = new ExponentialMovingAverage(100);

    private int _minTouches;
    private int _maxLevelAgeBars;
    private decimal _stopBufferAtr;
    private decimal _takeProfitR;
    private bool _useTrendFilter;
    private int _maxEntriesPerDay;
    private decimal? _riskPercent;
    private int _pivotBars;
    private decimal _mergeAtr;

    private DateTime _day = DateTime.MinValue;
    private int _entriesToday;

    public StrategyDescriptor Descriptor { get; } = new StrategyDescriptor("StructureSweep", "1.0.0");

    public int WarmupBars => 150;

    public void Initialize(StrategyContext context)
    {
        var parameters = context.Parameters;
        _pivotBars = parameters.GetInt("PivotBars", 3, min: 1, max: 20);
        _mergeAtr = parameters.GetDecimal("LevelMergeAtr", 0.3m, min: 0m, max: 5m);
        _minTouches = parameters.GetInt("MinTouches", 1, min: 1, max: 5);
        _maxLevelAgeBars = parameters.GetInt("MaxLevelAgeBars", 200, min: 10, max: 5000);
        _stopBufferAtr = parameters.GetDecimal("StopBufferAtr", 0.25m, min: 0m, max: 5m);
        _takeProfitR = parameters.GetDecimal("TakeProfitR", 2m, min: 0m, max: 20m);
        _useTrendFilter = parameters.GetBool("UseTrendFilter", true);
        _maxEntriesPerDay = parameters.GetInt("MaxEntriesPerDay", 2, min: 1, max: 20);

        var atrPeriod = parameters.GetInt("AtrPeriod", 14, min: 2, max: 200);
        var trendPeriod = parameters.GetInt("TrendMaPeriod", 100, min: 5, max: 500);
        var risk = parameters.GetDecimal("RiskPercent", 0m, min: 0m, max: 100m);
        _riskPercent = risk > 0m ? risk : (decimal?)null;

        _atr = new AverageTrueRange(atrPeriod);
        _trend = new ExponentialMovingAverage(trendPeriod);
        _structure = new SwingStructure(_pivotBars, _pivotBars);
    }

    public Signal? OnBar(MarketSnapshot snapshot)
    {
        var bar = snapshot.Current;

        if (snapshot.BarCloseTimeUtc.Date != _day)
        {
            _day = snapshot.BarCloseTimeUtc.Date;
            _entriesToday = 0;
        }

        // Erst auswerten, dann fortschreiben: Die Wendepunkte dieser Bar dürfen den Sweep
        // dieser Bar nicht mitbestimmen.
        var ready = _structure.IsReady && _atr.IsReady && _trend.IsReady;
        var trend = _trend.Value;
        var signal = ready && !snapshot.HasOpenPosition && _entriesToday < _maxEntriesPerDay
            ? Detect(bar, trend)
            : null;

        // Die Toleranz fuer gleiche Niveaus haengt an der Schwankungsbreite, nicht an einer
        // festen Zahl - dieselbe Regel soll auf Gold wie auf Bitcoin gelten.
        if (_atr.IsReady)
        {
            _structure.MergeTolerance = _atr.Value * _mergeAtr;
        }

        _structure.Update(bar);
        _atr.Update(bar);
        _trend.Update(bar.Close);

        if (signal != null)
        {
            _entriesToday++;
        }

        return signal;
    }

    private Signal? Detect(Candle bar, decimal trend)
    {
        var buffer = _atr.Value * _stopBufferAtr;

        foreach (var level in _structure.Lows)
        {
            if (level.Touches < _minTouches || Age(level) > _maxLevelAgeBars)
            {
                continue;
            }

            // Docht darunter, Schluss wieder darüber. Ein Schluss darunter wäre ein Bruch.
            if (bar.Low < level.Price && bar.Close > level.Price && (!_useTrendFilter || bar.Close > trend))
            {
                return Build(TradeDirection.Long, bar.Close, bar.Low - buffer, level);
            }
        }

        foreach (var level in _structure.Highs)
        {
            if (level.Touches < _minTouches || Age(level) > _maxLevelAgeBars)
            {
                continue;
            }

            if (bar.High > level.Price && bar.Close < level.Price && (!_useTrendFilter || bar.Close < trend))
            {
                return Build(TradeDirection.Short, bar.Close, bar.High + buffer, level);
            }
        }

        return null;
    }

    /// <summary>Alter des Niveaus in Bars. Ein Tief von vorletzter Woche raeumt niemand mehr ab.</summary>
    private long Age(SwingPoint level) => _structure.BarsSeen - level.BarIndex;

    private Signal? Build(TradeDirection direction, decimal price, decimal stop, SwingPoint level)
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
            $"Wendepunkt {level.Price:0.##} abgeraeumt ({level.Touches}x angelaufen)");
    }
}
