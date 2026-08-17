using System;
using Daytrading.Strategies.Indicators;
using Daytrading.Strategies.Model;

namespace Daytrading.Strategies.Library;

/// <summary>
/// Steigt nach Münzwurf ein. Keine Strategie, sondern das Lineal für alle anderen.
/// </summary>
/// <remarks>
/// Ohne diese Vergleichsgröße ist kein Ergebnis zu bewerten. Ein Erwartungswert von -0.3 R
/// klingt nach einem Fehler, solange man nicht weiß, was <em>gar keine Regel</em> auf denselben
/// Kursen mit denselben Kosten erreicht. Erst der Abstand zwischen beiden sagt, ob eine
/// Strategie etwas kann.
///
/// Der übliche Einwand lautet: Ein Münzwurf müsste bei null herauskommen. Das stimmt - vor
/// Kosten. Jeder Trade zahlt Spread und Kommission, und zwar unabhängig davon, ob die Richtung
/// stimmte. Diese Klasse macht sichtbar, wie groß dieser Abzug tatsächlich ist.
///
/// Ebenso wird eine zweite Vermutung überprüfbar: dass ein festes Chance-Risiko-Verhältnis
/// hilft. Bei Ziel 2R und Stop 1R ist es doppelt so wahrscheinlich, zuerst den Stop zu treffen -
/// die Trefferquote fällt auf rund ein Drittel, und der Erwartungswert bleibt null. Das
/// Verhältnis verschiebt die Trefferquote, es erzeugt keinen Vorteil. Über den Parameter
/// <c>TakeProfitR</c> lässt sich das hier direkt nachmessen.
///
/// Der Zufall ist ein eigener, aus dem Seed abgeleiteter Generator: <see cref="Random"/> ist
/// zwischen .NET-Versionen nicht stabil, und ein Backtest, der sich nicht wiederholen lässt,
/// ist keiner.
/// </remarks>
public sealed class RandomEntryStrategy : IStrategy
{
    private AverageTrueRange _atr = new AverageTrueRange(14);
    private ulong _state;
    private decimal _entryProbability;
    private decimal _stopAtrMultiple;
    private decimal _takeProfitR;
    private int _maxEntriesPerDay;
    private decimal? _riskPercent;
    private DateTime _day = DateTime.MinValue;
    private int _entriesToday;

    public StrategyDescriptor Descriptor { get; } = new StrategyDescriptor("RandomEntry", "1.0.0");

    public int WarmupBars => 30;

    public void Initialize(StrategyContext context)
    {
        var parameters = context.Parameters;
        _entryProbability = parameters.GetDecimal("EntryProbability", 0.02m, min: 0.0001m, max: 1m);
        _stopAtrMultiple = parameters.GetDecimal("StopAtrMultiple", 1.5m, min: 0.2m, max: 10m);
        _takeProfitR = parameters.GetDecimal("TakeProfitR", 2m, min: 0m, max: 20m);
        _maxEntriesPerDay = parameters.GetInt("MaxEntriesPerDay", 2, min: 1, max: 20);

        var atrPeriod = parameters.GetInt("AtrPeriod", 14, min: 2, max: 200);
        var risk = parameters.GetDecimal("RiskPercent", 0m, min: 0m, max: 100m);
        _riskPercent = risk > 0m ? risk : (decimal?)null;

        _atr = new AverageTrueRange(atrPeriod);
        _state = (ulong)context.RandomSeed | 1UL;
    }

    public Signal? OnBar(MarketSnapshot snapshot)
    {
        var bar = snapshot.Current;

        if (snapshot.BarCloseTimeUtc.Date != _day)
        {
            _day = snapshot.BarCloseTimeUtc.Date;
            _entriesToday = 0;
        }

        _atr.Update(bar);

        if (!_atr.IsReady || snapshot.HasOpenPosition || _entriesToday >= _maxEntriesPerDay)
        {
            return null;
        }

        if (NextUnit() >= _entryProbability)
        {
            return null;
        }

        var stopDistance = _atr.Value * _stopAtrMultiple;
        if (stopDistance <= 0m)
        {
            return null;
        }

        _entriesToday++;

        var direction = NextUnit() < 0.5m ? TradeDirection.Long : TradeDirection.Short;
        var sign = direction.Sign();
        var stop = bar.Close - (stopDistance * sign);
        var target = _takeProfitR > 0m ? bar.Close + (stopDistance * _takeProfitR * sign) : (decimal?)null;

        return Signal.Entry(direction, bar.Close, stop, target, _riskPercent, "Muenzwurf");
    }

    /// <summary>Gleichverteilter Wert in [0,1) aus einem xorshift-Generator.</summary>
    private decimal NextUnit()
    {
        _state ^= _state << 13;
        _state ^= _state >> 7;
        _state ^= _state << 17;
        return (decimal)(_state >> 11) / (decimal)(1UL << 53);
    }
}
