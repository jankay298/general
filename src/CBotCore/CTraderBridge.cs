using System;
using System.Collections.Generic;
using System.Globalization;
using cAlgo.API;
using cAlgo.API.Internals;
using Daytrading.Execution;
using Daytrading.Strategies;
using Daytrading.Execution.Sessions;
using Daytrading.Strategies.Model;
using Position = Daytrading.Strategies.Model.Position;
using SymbolInfo = Daytrading.Strategies.Model.SymbolInfo;

namespace Daytrading.CBot;

/// <summary>
/// Übersetzt zwischen cAlgo.API und den neutralen Typen des Frameworks.
/// </summary>
/// <remarks>
/// Das ist die gesamte cTrader-Abhängigkeit des Projekts. Alles darüber - Signale, Risiko,
/// Sessionregeln - arbeitet mit den eigenen Typen und ist ohne cTrader testbar.
/// </remarks>
internal static class CTraderBridge
{
    /// <summary>cAlgo-Bar in eine neutrale Kerze. Zeitstempel von cTrader sind UTC.</summary>
    public static Candle ToCandle(Bar bar) => new Candle(
        DateTime.SpecifyKind(bar.OpenTime, DateTimeKind.Utc),
        (decimal)bar.Open,
        (decimal)bar.High,
        (decimal)bar.Low,
        (decimal)bar.Close,
        bar.TickVolume);

    public static TradeType ToTradeType(TradeDirection direction) =>
        direction == TradeDirection.Long ? TradeType.Buy : TradeType.Sell;

    public static TradeDirection ToDirection(TradeType type) =>
        type == TradeType.Buy ? TradeDirection.Long : TradeDirection.Short;

    /// <summary>Offene cTrader-Position in die neutrale Form, für Snapshot und Risikoprüfung.</summary>
    public static Position ToPosition(cAlgo.API.Position position, string strategyTag) => new Position(
        position.Id.ToString(CultureInfo.InvariantCulture),
        position.SymbolName,
        ToDirection(position.TradeType),
        DateTime.SpecifyKind(position.EntryTime, DateTimeKind.Utc),
        (decimal)position.EntryPrice,
        (decimal)(position.StopLoss ?? position.EntryPrice),
        position.TakeProfit.HasValue ? (decimal)position.TakeProfit.Value : (decimal?)null,
        (decimal)position.VolumeInUnits,
        strategyTag);

    /// <summary>
    /// Handelsbedingungen des Symbols aus Sicht der Ausführungsschicht.
    /// </summary>
    /// <remarks>
    /// Der Punktwert wird aus <c>PipValue / PipSize</c> abgeleitet: Wert einer Preisbewegung
    /// von 1.0 je Einheit in Kontowährung. Vor dem ersten Livebetrieb einmal gegen eine echte
    /// Position auf dem Demokonto prüfen - eine falsche Umrechnung verschiebt jede
    /// Positionsgröße.
    /// </remarks>
    public static ExecutionSymbol ToExecutionSymbol(Symbol symbol, AssetClass assetClass)
    {
        var valuePerPoint = symbol.PipSize > 0 ? symbol.PipValue / symbol.PipSize : 1d;

        return new ExecutionSymbol(
            new SymbolInfo(symbol.Name, assetClass, (decimal)symbol.TickSize),
            (decimal)symbol.VolumeInUnitsMin,
            (decimal)symbol.VolumeInUnitsStep,
            (decimal)symbol.VolumeInUnitsMax,
            (decimal)Math.Max(valuePerPoint, 0.0000001d),
            (decimal)(symbol.Spread > 0 ? symbol.Spread : 0d),
            (decimal)Math.Max(symbol.Commission, 0d));
    }

    /// <summary>Baut den Sessionkalender aus den cBot-Parametern - dieselbe Klasse wie im Backtest.</summary>
    public static TradingSessionCalendar ToSessionCalendar(
        string timeZoneId,
        string sessionStart,
        string sessionEnd,
        string tradingDays,
        string holidayCalendar,
        string symbolName)
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById(string.IsNullOrWhiteSpace(timeZoneId) ? "UTC" : timeZoneId);
        var days = new List<DayOfWeek>();

        foreach (var day in (tradingDays ?? string.Empty).Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries))
        {
            if (!Enum.TryParse<DayOfWeek>(day.Trim(), ignoreCase: true, out var parsed))
            {
                throw new ArgumentException($"'{day.Trim()}' ist kein Wochentag.", nameof(tradingDays));
            }

            days.Add(parsed);
        }

        if (days.Count == 0)
        {
            days.AddRange(new[]
            {
                DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday,
            });
        }

        return new TradingSessionCalendar(
            zone,
            ParseTimeOfDay(sessionStart, "Sessionbeginn"),
            ParseTimeOfDay(sessionEnd, "Sessionende"),
            days,
            UsEquityHolidayCalendar.Resolve(holidayCalendar),
            symbolName);
    }

    public static TimeSpan ParseTimeOfDay(string text, string label)
    {
        if (string.Equals(text, "24:00", StringComparison.Ordinal))
        {
            return TimeSpan.FromHours(24);
        }

        if (!TimeSpan.TryParse(text, CultureInfo.InvariantCulture, out var value))
        {
            throw new ArgumentException($"{label}: '{text}' ist keine Uhrzeit im Format HH:mm.", nameof(text));
        }

        return value;
    }
}

/// <summary>Leitet Meldungen der Strategie- und Risikoschicht in das cTrader-Log.</summary>
internal sealed class CTraderLog : IStrategyLog
{
    private readonly Action<string> _print;
    private readonly bool _verbose;

    public CTraderLog(Action<string> print, bool verbose)
    {
        _print = print ?? throw new ArgumentNullException(nameof(print));
        _verbose = verbose;
    }

    public void Write(StrategyLogLevel level, string message)
    {
        if (level == StrategyLogLevel.Debug && !_verbose)
        {
            return;
        }

        _print($"[{level}] {message}");
    }
}
