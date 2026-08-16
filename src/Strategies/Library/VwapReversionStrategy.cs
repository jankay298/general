using System;
using System.Globalization;
using Daytrading.Strategies.Indicators;
using Daytrading.Strategies.Model;

namespace Daytrading.Strategies.Library;

/// <summary>
/// Rückkehr zum sessionverankerten VWAP: Weicht der Kurs weit genug vom volumengewichteten
/// Durchschnittspreis des Tages ab, wird auf die Rückkehr dorthin gesetzt.
/// </summary>
/// <remarks>
/// Die zweite Beispielstrategie, und zwar bewusst mit gegensätzlichem Charakter zum
/// Opening-Range-Breakout: Die eine handelt Ausbrüche, die andere deren Scheitern. Ob eine von
/// beiden auf einem Symbol funktioniert, entscheidet der Backtest - nicht die Erwartung.
///
/// Sie zeigt außerdem zwei Dinge, die der ORB nicht braucht: sessionverankerte Zustandsführung
/// (VWAP und Streuung werden bei jedem Sessionbeginn zurückgesetzt) und den aktiven Ausstieg
/// über <see cref="SignalKind.CloseAll"/>, sobald der Kurs den VWAP wieder erreicht.
///
/// Fehlt Volumen in den Daten - bei manchen CFD-Quellen der Fall - fällt die Gewichtung
/// automatisch auf gleiche Gewichte zurück. Das wird protokolliert, statt still einen VWAP
/// auszurechnen, der keiner ist.
///
/// Parameter (Defaults in Klammern):
/// <list type="bullet">
///   <item><c>BandSigma</c> (2.0) - Abstand vom VWAP in Standardabweichungen für den Einstieg</item>
///   <item><c>MinBarsForVwap</c> (12) - Bars seit Sessionbeginn, bevor gehandelt wird</item>
///   <item><c>AtrPeriod</c> (14), <c>StopAtrMultiple</c> (1.5) - Stopabstand</item>
///   <item><c>MaxEntriesPerDay</c> (2) - Begrenzung der Versuche je Handelstag</item>
///   <item><c>ExitAtVwap</c> (true) - aktiver Ausstieg bei Rückkehr zum VWAP</item>
///   <item><c>AllowLong</c> (true), <c>AllowShort</c> (true), <c>RiskPercent</c> (0)</item>
/// </list>
/// </remarks>
public sealed class VwapReversionStrategy : IStrategy
{
    private static readonly StrategyDescriptor StaticDescriptor = new StrategyDescriptor(
        "VwapReversion",
        "1.0.0",
        "Rückkehr zum sessionverankerten VWAP nach Abweichung um mehrere Standardabweichungen.");

    private SymbolInfo? _symbol;
    private IStrategyLog _log = NullStrategyLog.Instance;
    private AverageTrueRange? _atr;

    private decimal _bandSigma;
    private int _minBars;
    private int _atrPeriod;
    private decimal _stopAtrMultiple;
    private int _maxEntriesPerDay;
    private bool _exitAtVwap;
    private bool _allowLong;
    private bool _allowShort;
    private decimal? _riskPercent;

    // Sessionverankerter Zustand.
    private DateTime? _tradingDay;
    private decimal _volumeSum;
    private decimal _weightedPriceSum;
    private decimal _weightedSquareSum;
    private int _barsInSession;
    private int _entriesToday;
    private bool _volumeMissingReported;

    public StrategyDescriptor Descriptor => StaticDescriptor;

    public int WarmupBars => _atrPeriod + 1;

    public void Initialize(StrategyContext context)
    {
        if (context == null)
        {
            throw new ArgumentNullException(nameof(context));
        }

        _symbol = context.Symbol;
        _log = context.Log;

        var parameters = context.Parameters;
        _bandSigma = parameters.GetDecimal("BandSigma", 2.0m, min: 0.1m, max: 10m);
        _minBars = parameters.GetInt("MinBarsForVwap", 12, min: 2, max: 500);
        _atrPeriod = parameters.GetInt("AtrPeriod", 14, min: 1, max: 500);
        _stopAtrMultiple = parameters.GetDecimal("StopAtrMultiple", 1.5m, min: 0.1m, max: 20m);
        _maxEntriesPerDay = parameters.GetInt("MaxEntriesPerDay", 2, min: 1, max: 50);
        _exitAtVwap = parameters.GetBool("ExitAtVwap", true);
        _allowLong = parameters.GetBool("AllowLong", true);
        _allowShort = parameters.GetBool("AllowShort", true);

        var risk = parameters.GetDecimal("RiskPercent", 0m, min: 0m, max: 100m);
        _riskPercent = risk > 0m ? risk : (decimal?)null;

        if (!_allowLong && !_allowShort)
        {
            throw new StrategyParameterException(
                "AllowLong und AllowShort sind beide false - die Strategie könnte nie handeln.");
        }

        _atr = new AverageTrueRange(_atrPeriod);
        ResetSession();
        _tradingDay = null;
    }

    public Signal? OnBar(MarketSnapshot snapshot)
    {
        var symbol = _symbol ?? throw new InvalidOperationException(
            "Initialize wurde nicht aufgerufen, bevor OnBar verwendet wurde.");
        var atr = _atr!;

        var bar = snapshot.Current;
        atr.Update(bar);

        if (_tradingDay != snapshot.Session.TradingDay)
        {
            _tradingDay = snapshot.Session.TradingDay;
            ResetSession();
        }

        if (bar.OpenTimeUtc < snapshot.Session.StartUtc)
        {
            return null;
        }

        Accumulate(bar);

        if (_barsInSession < _minBars || !atr.IsReady)
        {
            return null;
        }

        var vwap = Vwap();
        var deviation = StandardDeviation(vwap);
        if (deviation <= 0m)
        {
            return null;
        }

        var close = bar.Close;

        if (snapshot.HasOpenPosition)
        {
            if (!_exitAtVwap)
            {
                return null;
            }

            // Ziel erreicht: Der Kurs ist am VWAP angekommen. Der Ausstieg wird angefordert,
            // ausgeführt wird er von der Ausführungsschicht.
            var position = snapshot.OpenPositions[0];
            var reachedMean = position.Direction == TradeDirection.Long ? close >= vwap : close <= vwap;
            return reachedMean
                ? Signal.CloseAll(close, FormatReason("VWAP erreicht", vwap, deviation))
                : null;
        }

        if (_entriesToday >= _maxEntriesPerDay)
        {
            return null;
        }

        var upper = vwap + _bandSigma * deviation;
        var lower = vwap - _bandSigma * deviation;

        TradeDirection direction;
        if (_allowLong && close < lower)
        {
            direction = TradeDirection.Long;
        }
        else if (_allowShort && close > upper)
        {
            direction = TradeDirection.Short;
        }
        else
        {
            return null;
        }

        var stopDistance = atr.Value * _stopAtrMultiple;
        if (stopDistance < symbol.TickSize)
        {
            _log.Warning(FormatReason("Stopabstand kleiner als ein Tick, kein Signal", vwap, deviation));
            return null;
        }

        var stop = direction == TradeDirection.Long
            ? symbol.RoundDownToTick(close - stopDistance)
            : symbol.RoundUpToTick(close + stopDistance);

        // Ziel ist der VWAP selbst. Liegt er näher als ein Tick, lohnt der Trade nicht.
        var target = symbol.RoundToTick(vwap);
        if (Math.Abs(target - close) < symbol.TickSize)
        {
            return null;
        }

        _entriesToday++;
        return Signal.Entry(direction, close, stop, target, _riskPercent, FormatReason(
            direction == TradeDirection.Long ? "unter unterem Band" : "über oberem Band", vwap, deviation));
    }

    private void Accumulate(Candle bar)
    {
        var price = bar.TypicalPrice;

        // Ohne Volumen ist ein volumengewichteter Durchschnitt nicht berechenbar. Statt still
        // etwas anderes auszurechnen, wird auf gleiche Gewichte umgestellt und das gemeldet.
        var weight = bar.Volume;
        if (weight <= 0m)
        {
            weight = 1m;
            if (!_volumeMissingReported)
            {
                _volumeMissingReported = true;
                _log.Warning(
                    "[VWAP] Die Daten enthalten kein Volumen. Der VWAP wird ersatzweise ungewichtet berechnet - " +
                    "das ist kein VWAP im eigentlichen Sinn und gehört in den Ergebnisbericht.");
            }
        }

        _volumeSum += weight;
        _weightedPriceSum += price * weight;
        _weightedSquareSum += price * price * weight;
        _barsInSession++;
    }

    private decimal Vwap() => _volumeSum <= 0m ? 0m : _weightedPriceSum / _volumeSum;

    private decimal StandardDeviation(decimal vwap)
    {
        if (_volumeSum <= 0m)
        {
            return 0m;
        }

        var variance = _weightedSquareSum / _volumeSum - vwap * vwap;
        return variance <= 0m ? 0m : (decimal)Math.Sqrt((double)variance);
    }

    private void ResetSession()
    {
        _volumeSum = 0m;
        _weightedPriceSum = 0m;
        _weightedSquareSum = 0m;
        _barsInSession = 0;
        _entriesToday = 0;
    }

    private string FormatReason(string what, decimal vwap, decimal deviation) =>
        string.Format(
            CultureInfo.InvariantCulture,
            "VWAP-Reversion: {0} (VWAP {1:0.####}, Streuung {2:0.####}, {3} Sigma)",
            what, vwap, deviation, _bandSigma);
}
