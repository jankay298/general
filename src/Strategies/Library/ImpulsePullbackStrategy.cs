using System;
using Daytrading.Strategies.Indicators;
using Daytrading.Strategies.Model;

namespace Daytrading.Strategies.Library;

/// <summary>
/// Wartet nach einer kräftigen Bewegung auf den Rücklauf und steigt dann in Richtung der
/// Bewegung ein.
/// </summary>
/// <remarks>
/// Die von Hand gehandelte Regel dahinter: Läuft Gold von 4000 auf 4100, wird nicht oben
/// gekauft, sondern auf 4040 bis 4050 gewartet.
///
/// Unterschied zu <see cref="TrendPullbackStrategy"/>, und der Grund, warum das eine eigene
/// Klasse ist: Dort war der Rücksetzer die Berührung eines gleitenden Durchschnitts, hier ist
/// er ein <b>Anteil einer gemessenen Bewegung</b>. Der Durchschnitt weiß nichts davon, wie weit
/// der Markt gerade gelaufen ist; diese Regel misst genau das und macht die Größe der Bewegung
/// zur Bedingung. Ein Rücklauf von 50 % auf 100 Dollar ist etwas anderes als 50 % auf 8 Dollar.
///
/// Drei Bedingungen, alle beim Einstieg bekannt:
/// <list type="number">
///   <item>Eine Bewegung von mindestens <c>MinImpulseAtr</c> ATR innerhalb von
///         <c>ImpulseLookback</c> Bars.</item>
///   <item>Ein Rücklauf in das Band zwischen <c>MinRetracement</c> und <c>MaxRetracement</c>
///         dieser Bewegung. Zu wenig heißt, der Markt hat sich nicht wirklich erholt; zu viel
///         heißt, die Bewegung ist widerrufen.</item>
///   <item>Eine Bestätigung: Der Schlusskurs dreht wieder in die Richtung der Bewegung. Ohne sie
///         greift die Regel in jeden durchlaufenden Rücksetzer.</item>
/// </list>
///
/// Der Stop sitzt jenseits des Ursprungs der Bewegung: Wird der unterschritten, war die
/// Annahme falsch - und nicht bloß der Einstieg unglücklich.
/// </remarks>
public sealed class ImpulsePullbackStrategy : IStrategy
{
    private AverageTrueRange _atr = new AverageTrueRange(14);
    private RollingWindow<Candle> _window = new RollingWindow<Candle>(24);

    private int _lookback;
    private decimal _minImpulseAtr;
    private decimal _minRetracement;
    private decimal _maxRetracement;
    private decimal _stopBeyondOriginAtr;
    private decimal _takeProfitR;
    private int _maxEntriesPerDay;
    private decimal? _riskPercent;

    private DateTime _day = DateTime.MinValue;
    private int _entriesToday;
    private Candle? _previous;

    public StrategyDescriptor Descriptor { get; } = new StrategyDescriptor("ImpulsePullback", "1.0.0");

    public int WarmupBars => Math.Max(_lookback, 14) + 10;

    public void Initialize(StrategyContext context)
    {
        var parameters = context.Parameters;
        _lookback = parameters.GetInt("ImpulseLookback", 24, min: 3, max: 200);
        _minImpulseAtr = parameters.GetDecimal("MinImpulseAtr", 3m, min: 0.5m, max: 50m);
        _minRetracement = parameters.GetDecimal("MinRetracement", 0.38m, min: 0.05m, max: 0.95m);
        _maxRetracement = parameters.GetDecimal("MaxRetracement", 0.66m, min: 0.1m, max: 0.99m);
        _stopBeyondOriginAtr = parameters.GetDecimal("StopBeyondOriginAtr", 0.5m, min: 0m, max: 10m);
        _takeProfitR = parameters.GetDecimal("TakeProfitR", 2m, min: 0m, max: 20m);
        _maxEntriesPerDay = parameters.GetInt("MaxEntriesPerDay", 2, min: 1, max: 20);

        var atrPeriod = parameters.GetInt("AtrPeriod", 14, min: 2, max: 200);
        var risk = parameters.GetDecimal("RiskPercent", 0m, min: 0m, max: 100m);
        _riskPercent = risk > 0m ? risk : (decimal?)null;

        if (_maxRetracement <= _minRetracement)
        {
            throw new StrategyParameterException(
                $"MaxRetracement ({_maxRetracement}) muss größer als MinRetracement ({_minRetracement}) sein.");
        }

        _atr = new AverageTrueRange(atrPeriod);
        _window = new RollingWindow<Candle>(_lookback);
    }

    public Signal? OnBar(MarketSnapshot snapshot)
    {
        var bar = snapshot.Current;

        if (snapshot.BarCloseTimeUtc.Date != _day)
        {
            _day = snapshot.BarCloseTimeUtc.Date;
            _entriesToday = 0;
        }

        var ready = _window.IsFull && _atr.IsReady && _previous.HasValue;
        var previous = _previous;

        // Die Bewegung wird aus den Bars VOR der laufenden gemessen; die laufende ist der
        // mögliche Einstieg und darf die Bewegung nicht mitbestimmen.
        var high = decimal.MinValue;
        var low = decimal.MaxValue;
        var highIndex = -1;
        var lowIndex = -1;

        for (var i = 0; i < _window.Count; i++)
        {
            var candle = _window[i];
            if (candle.High > high)
            {
                high = candle.High;
                highIndex = i;
            }

            if (candle.Low < low)
            {
                low = candle.Low;
                lowIndex = i;
            }
        }

        _window.Add(bar);
        _atr.Update(bar);
        _previous = bar;

        if (!ready || snapshot.HasOpenPosition || _entriesToday >= _maxEntriesPerDay)
        {
            return null;
        }

        var range = high - low;
        if (range < _atr.Value * _minImpulseAtr || _atr.Value <= 0m)
        {
            return null;
        }

        // Ein niedrigerer Index bedeutet jünger: Liegt das Hoch nach dem Tief, ging es aufwärts.
        var upward = highIndex < lowIndex;

        if (upward)
        {
            var retracement = (high - bar.Low) / range;
            var turnedBack = bar.Close > previous!.Value.Close;

            if (retracement >= _minRetracement && retracement <= _maxRetracement && turnedBack)
            {
                _entriesToday++;
                var stop = low - (_atr.Value * _stopBeyondOriginAtr);
                return Build(TradeDirection.Long, bar.Close, stop, range, retracement);
            }
        }
        else
        {
            var retracement = (bar.High - low) / range;
            var turnedBack = bar.Close < previous!.Value.Close;

            if (retracement >= _minRetracement && retracement <= _maxRetracement && turnedBack)
            {
                _entriesToday++;
                var stop = high + (_atr.Value * _stopBeyondOriginAtr);
                return Build(TradeDirection.Short, bar.Close, stop, range, retracement);
            }
        }

        return null;
    }

    private Signal? Build(TradeDirection direction, decimal price, decimal stop, decimal range, decimal retracement)
    {
        var sign = direction.Sign();
        var stopDistance = (price - stop) * sign;

        // Sitzt der Stop bereits auf der falschen Seite, ist der Rücklauf schon zu weit gelaufen.
        if (stopDistance <= 0m)
        {
            return null;
        }

        var target = _takeProfitR > 0m ? price + (stopDistance * _takeProfitR * sign) : (decimal?)null;
        return Signal.Entry(
            direction,
            price,
            stop,
            target,
            _riskPercent,
            $"Ruecklauf {retracement:P0} einer Bewegung von {range:0.##}");
    }
}
