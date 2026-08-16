using System;
using System.Collections.Generic;
using Daytrading.Execution;
using Daytrading.Strategies.Model;

namespace Daytrading.Execution.Tests.TestSupport;

/// <summary>
/// Bausteine für die Tests der Ausführungsschicht. Bewusst eine US-Aktiensession
/// (13:30-20:00 UTC), 15-Minuten-Bars und ein Konto über 10.000, damit sich Prozentwerte
/// im Kopf nachrechnen lassen: 1 % Risiko sind 100.
/// </summary>
internal static class Fixture
{
    public const decimal StartingBalance = 10_000m;

    public static readonly DateTime Day = new DateTime(2024, 3, 1, 0, 0, 0, DateTimeKind.Utc);

    public static DateTime At(int hour, int minute) => Day.AddHours(hour).AddMinutes(minute);

    public static TradingSession Session(DateTime? day = null)
    {
        var d = day ?? Day;
        return new TradingSession(d, d.AddHours(13).AddMinutes(30), d.AddHours(20));
    }

    public static ExecutionSymbol Symbol(
        decimal tickSize = 0.01m,
        decimal minQuantity = 1m,
        decimal quantityStep = 1m,
        decimal maxQuantity = 1_000_000m,
        decimal valuePerPricePointPerUnit = 1m) =>
        new ExecutionSymbol(
            new SymbolInfo("TEST", AssetClass.Equity, tickSize),
            minQuantity,
            quantityStep,
            maxQuantity,
            valuePerPricePointPerUnit);

    public static RiskLimits Limits() => RiskLimits.Default;

    public static AccountState Account() => new AccountState(StartingBalance);

    public static Candle Bar(DateTime openTimeUtc, decimal close) =>
        new Candle(openTimeUtc, close, close + 0.5m, close - 0.5m, close, 1_000m);

    /// <summary>Snapshot einer 15-Minuten-Bar. <paramref name="barOpenUtc"/> ist der Bar-Beginn.</summary>
    public static MarketSnapshot Snapshot(
        DateTime barOpenUtc,
        decimal close,
        IReadOnlyList<Position>? openPositions = null,
        DateTime? sessionDay = null,
        ExecutionSymbol? symbol = null)
    {
        var series = new BarSeries();
        series.Append(Bar(barOpenUtc, close));
        return new MarketSnapshot(
            (symbol ?? Symbol()).Info,
            Timeframe.M15,
            series,
            Session(sessionDay),
            openPositions);
    }

    public static Position Position(
        string id = "P1",
        TradeDirection direction = TradeDirection.Long,
        decimal entryPrice = 100m,
        decimal stopLoss = 98m,
        decimal quantity = 10m,
        DateTime? entryTimeUtc = null,
        decimal? takeProfit = null) =>
        new Position(
            id,
            "TEST",
            direction,
            entryTimeUtc ?? At(14, 0),
            entryPrice,
            stopLoss,
            takeProfit,
            quantity,
            "ORB");

    public static List<OpenRiskItem> OpenRisk(decimal currentPrice, params Position[] positions)
    {
        var items = new List<OpenRiskItem>(positions.Length);
        foreach (var position in positions)
        {
            items.Add(new OpenRiskItem(position, currentPrice, 1m));
        }

        return items;
    }

    public static Signal LongEntry(decimal price = 100m, decimal stop = 98m, decimal? takeProfit = null, decimal? riskPercent = null) =>
        Signal.Entry(TradeDirection.Long, price, stop, takeProfit, riskPercent, "test long");

    public static Signal ShortEntry(decimal price = 100m, decimal stop = 102m, decimal? takeProfit = null, decimal? riskPercent = null) =>
        Signal.Entry(TradeDirection.Short, price, stop, takeProfit, riskPercent, "test short");
}
