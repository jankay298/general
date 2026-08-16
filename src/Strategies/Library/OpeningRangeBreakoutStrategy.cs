using System;
using System.Globalization;
using Daytrading.Strategies.Indicators;
using Daytrading.Strategies.Model;

namespace Daytrading.Strategies.Library;

/// <summary>Woraus der Stop-Loss des Opening-Range-Breakouts berechnet wird.</summary>
public enum OpeningRangeStopMode
{
    /// <summary>Gegenüberliegende Seite der Eröffnungsrange. Klassische Variante.</summary>
    OppositeRangeSide = 0,

    /// <summary>Vielfaches des ATR. Unabhängig von der Rangebreite, dafür abhängig von der Volatilität.</summary>
    AtrMultiple = 1,
}

/// <summary>
/// Opening-Range-Breakout: In den ersten Minuten der Session wird eine Preisspanne
/// gebildet; der erste Bar-Schluss darüber oder darunter erzeugt ein Signal in
/// Ausbruchsrichtung.
/// </summary>
/// <remarks>
/// Beispielstrategie, um die Trennung zwischen Strategie und Ausführung zu zeigen.
/// Sie berechnet Ein- und Ausstiegspreise, aber weder Positionsgröße noch Zwangsschließung
/// vor Sessionende, weder Tagesverlustgrenze noch maximale Haltedauer - das alles liegt in
/// der Ausführungsschicht und lässt sich von hier aus nicht umgehen.
///
/// Kein Look-ahead: Die Range wird nur aus Bars gebildet, die vollständig im Zeitfenster
/// liegen, und Ausbrüche werden erst ab der ersten Bar <em>nach</em> dem Fenster geprüft.
/// Das Signal entsteht auf dem Close der Signalbar; ausgeführt wird es frühestens auf der
/// Open der Folgebar - dafür sorgt der Host.
///
/// Parameter (alle optional, Defaults in Klammern):
/// <list type="bullet">
///   <item><c>OpeningRangeMinutes</c> (30) - Länge des Eröffnungsfensters ab Sessionbeginn</item>
///   <item><c>BreakoutBufferTicks</c> (0) - Mindestabstand über/unter der Range in Ticks</item>
///   <item><c>StopLossMode</c> (OppositeRangeSide) - OppositeRangeSide oder AtrMultiple</item>
///   <item><c>AtrPeriod</c> (14), <c>AtrStopMultiple</c> (1.5) - nur für AtrMultiple</item>
///   <item><c>TakeProfitR</c> (2.0) - Ziel als Vielfaches des Stopabstands, 0 = kein Ziel</item>
///   <item><c>OneTradePerDay</c> (true) - nur der erste Ausbruch je Handelstag</item>
///   <item><c>AllowLong</c> (true), <c>AllowShort</c> (true) - Richtungsfilter</item>
///   <item><c>RiskPercent</c> (0) - Risikowunsch je Trade, 0 = Default der Ausführungsschicht</item>
/// </list>
/// </remarks>
public sealed class OpeningRangeBreakoutStrategy : IStrategy
{
    private static readonly StrategyDescriptor StaticDescriptor = new StrategyDescriptor(
        "OpeningRangeBreakout",
        "1.0.0",
        "Ausbruch aus der Eröffnungsrange der Session, Stop an der Gegenseite oder per ATR.");

    private SymbolInfo? _symbol;
    private IStrategyLog _log = NullStrategyLog.Instance;
    private AverageTrueRange? _atr;

    private int _openingRangeMinutes;
    private int _breakoutBufferTicks;
    private OpeningRangeStopMode _stopMode;
    private int _atrPeriod;
    private decimal _atrStopMultiple;
    private decimal _takeProfitR;
    private bool _oneTradePerDay;
    private bool _allowLong;
    private bool _allowShort;
    private decimal? _riskPercent;

    // Zustand je Handelstag. Wird beim Tageswechsel zurückgesetzt.
    private DateTime? _tradingDay;
    private DateTime _rangeEndUtc;
    private decimal _rangeHigh;
    private decimal _rangeLow;
    private bool _rangeHasBars;
    private bool _signalledToday;

    public StrategyDescriptor Descriptor => StaticDescriptor;

    public int WarmupBars => _stopMode == OpeningRangeStopMode.AtrMultiple ? _atrPeriod + 1 : 0;

    public void Initialize(StrategyContext context)
    {
        if (context == null)
        {
            throw new ArgumentNullException(nameof(context));
        }

        _symbol = context.Symbol;
        _log = context.Log;

        var parameters = context.Parameters;
        _openingRangeMinutes = parameters.GetInt("OpeningRangeMinutes", 30, min: 1, max: 480);
        _breakoutBufferTicks = parameters.GetInt("BreakoutBufferTicks", 0, min: 0, max: 10_000);
        _stopMode = parameters.GetEnum("StopLossMode", OpeningRangeStopMode.OppositeRangeSide);
        _atrPeriod = parameters.GetInt("AtrPeriod", 14, min: 1, max: 500);
        _atrStopMultiple = parameters.GetDecimal("AtrStopMultiple", 1.5m, min: 0.1m, max: 20m);
        _takeProfitR = parameters.GetDecimal("TakeProfitR", 2.0m, min: 0m, max: 100m);
        _oneTradePerDay = parameters.GetBool("OneTradePerDay", true);
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
        ResetDayState();
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
            ResetDayState();
            _rangeEndUtc = snapshot.Session.StartUtc + TimeSpan.FromMinutes(_openingRangeMinutes);
        }

        var barCloseUtc = snapshot.BarCloseTimeUtc;

        // Bars vor Sessionbeginn (z.B. vorbörslich gelieferte Daten) gehören nicht zur Range.
        if (bar.OpenTimeUtc < snapshot.Session.StartUtc)
        {
            return null;
        }

        // Eröffnungsfenster: Bar zählt nur, wenn sie vollständig darin liegt.
        if (barCloseUtc <= _rangeEndUtc)
        {
            if (_rangeHasBars)
            {
                _rangeHigh = Math.Max(_rangeHigh, bar.High);
                _rangeLow = Math.Min(_rangeLow, bar.Low);
            }
            else
            {
                _rangeHigh = bar.High;
                _rangeLow = bar.Low;
                _rangeHasBars = true;
            }

            return null;
        }

        if (!_rangeHasBars)
        {
            // Kommt vor, wenn die Bargröße nicht in das Eröffnungsfenster passt oder der
            // Handelstag verkürzt war. Lieber kein Trade als ein Trade auf einer Range,
            // die es nicht gibt.
            _log.Debug(FormatMessage(
                symbol, snapshot, "keine vollständige Bar im Eröffnungsfenster, heute kein Ausbruchssignal"));
            return null;
        }

        if (_signalledToday && _oneTradePerDay)
        {
            return null;
        }

        if (snapshot.HasOpenPosition)
        {
            return null;
        }

        if (_stopMode == OpeningRangeStopMode.AtrMultiple && !atr.IsReady)
        {
            _log.Debug(FormatMessage(symbol, snapshot, $"ATR({_atrPeriod}) noch nicht aufgewärmt"));
            return null;
        }

        var buffer = _breakoutBufferTicks * symbol.TickSize;
        var close = bar.Close;

        TradeDirection direction;
        if (_allowLong && close > _rangeHigh + buffer)
        {
            direction = TradeDirection.Long;
        }
        else if (_allowShort && close < _rangeLow - buffer)
        {
            direction = TradeDirection.Short;
        }
        else
        {
            return null;
        }

        var stopLoss = CalculateStopLoss(symbol, direction, close, atr);
        var stopDistance = Math.Abs(close - stopLoss);

        if (stopDistance < symbol.TickSize)
        {
            // Ohne belastbaren Stopabstand kein Signal. Ein Default-Stop wäre hier bequem
            // und falsch: die Positionsgröße käme aus einer erfundenen Zahl.
            _log.Warning(FormatMessage(
                symbol,
                snapshot,
                string.Format(
                    CultureInfo.InvariantCulture,
                    "Ausbruch {0} verworfen: Stopabstand {1} kleiner als ein Tick ({2})",
                    direction, stopDistance, symbol.TickSize)));
            return null;
        }

        decimal? takeProfit = null;
        if (_takeProfitR > 0m)
        {
            takeProfit = symbol.RoundToTick(close + direction.Sign() * stopDistance * _takeProfitR);
        }

        _signalledToday = true;

        var reason = string.Format(
            CultureInfo.InvariantCulture,
            "ORB {0} break of {1}m range [{2}, {3}], stop {4}",
            direction == TradeDirection.Long ? "long" : "short",
            _openingRangeMinutes,
            _rangeLow,
            _rangeHigh,
            _stopMode);

        return Signal.Entry(direction, close, stopLoss, takeProfit, _riskPercent, reason);
    }

    private decimal CalculateStopLoss(SymbolInfo symbol, TradeDirection direction, decimal close, AverageTrueRange atr)
    {
        decimal raw;
        if (_stopMode == OpeningRangeStopMode.AtrMultiple)
        {
            raw = close - direction.Sign() * atr.Value * _atrStopMultiple;
        }
        else
        {
            raw = direction == TradeDirection.Long ? _rangeLow : _rangeHigh;
        }

        // Zur sicheren Seite runden, damit der Stop nie durch Rundung näher an den
        // Einstieg rutscht als beabsichtigt.
        return direction == TradeDirection.Long ? symbol.RoundDownToTick(raw) : symbol.RoundUpToTick(raw);
    }

    private void ResetDayState()
    {
        _rangeHigh = 0m;
        _rangeLow = 0m;
        _rangeHasBars = false;
        _signalledToday = false;
        _rangeEndUtc = DateTime.MinValue;
    }

    private static string FormatMessage(SymbolInfo symbol, MarketSnapshot snapshot, string message) =>
        string.Format(
            CultureInfo.InvariantCulture,
            "[ORB] {0} {1:yyyy-MM-dd HH:mm} UTC: {2}",
            symbol.Name, snapshot.BarCloseTimeUtc, message);
}
