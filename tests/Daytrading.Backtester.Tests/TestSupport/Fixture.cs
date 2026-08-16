using Daytrading.Data.Config;
using Daytrading.Strategies;
using Daytrading.Strategies.Model;

namespace Daytrading.Backtester.Tests.TestSupport;

/// <summary>
/// Bausteine für die Backtester-Tests. Die Session liegt bewusst in UTC (09:00-17:00), damit
/// keine Zeitumstellung die erwarteten Zeitstempel verschiebt.
/// </summary>
internal static class Fixture
{
    public static readonly DateTime Monday = new DateTime(2024, 3, 4, 0, 0, 0, DateTimeKind.Utc);

    public static SymbolConfig Symbol(decimal spread = 0.02m, decimal commission = 0m) => new SymbolConfig
    {
        Name = "TEST",
        AssetClass = AssetClass.Equity,
        TimeZone = "UTC",
        SessionStart = "09:00",
        SessionEnd = "17:00",
        HolidayCalendar = "none",
        WorkingTimeframe = "M15",
        TickSize = 0.01m,
        MinQuantity = 1m,
        QuantityStep = 1m,
        MaxQuantity = 1_000_000m,
        ValuePerPricePointPerUnit = 1m,
        TypicalSpread = spread,
        CommissionPerUnitPerSide = commission,
        ThinLiquiditySpreadFactor = 1m,
    };

    public static BacktestOptions Options() => new BacktestOptions
    {
        StartingBalance = 100_000m,
        SlippageTicks = 1m,
        StopSlippageTicks = 3m,
        MinimumSampleTrades = 30,
    };

    /// <summary>Bars eines Handelstages ab 09:00 UTC, 15 Minuten je Bar.</summary>
    public static List<Candle> Session(DateTime day, params (decimal Open, decimal High, decimal Low, decimal Close)[] bars)
    {
        var start = day.Date.AddHours(9);
        var result = new List<Candle>(bars.Length);
        for (var i = 0; i < bars.Length; i++)
        {
            var bar = bars[i];
            result.Add(new Candle(start.AddMinutes(15 * i), bar.Open, bar.High, bar.Low, bar.Close, 1_000m));
        }

        return result;
    }

    /// <summary>Ruhige Bars ohne Bewegung - Kulisse, wenn nur ein bestimmter Moment interessiert.</summary>
    public static List<Candle> FlatSession(DateTime day, int count, decimal price = 100m)
    {
        var bars = new (decimal, decimal, decimal, decimal)[count];
        for (var i = 0; i < count; i++)
        {
            bars[i] = (price, price + 0.05m, price - 0.05m, price);
        }

        return Session(day, bars);
    }
}

/// <summary>Strategie, die auf einer festgelegten Bar ein Signal liefert - und sonst nichts.</summary>
internal sealed class ScriptedStrategy : IStrategy
{
    private readonly Dictionary<int, Func<MarketSnapshot, Signal?>> _script;
    private int _bar;

    public ScriptedStrategy(Dictionary<int, Func<MarketSnapshot, Signal?>> script)
    {
        _script = script;
    }

    public static ScriptedStrategy EntryAt(int barIndex, TradeDirection direction, decimal stop, decimal? takeProfit = null) =>
        new ScriptedStrategy(new Dictionary<int, Func<MarketSnapshot, Signal?>>
        {
            [barIndex] = snapshot => Signal.Entry(direction, snapshot.Current.Close, stop, takeProfit, null, "scripted"),
        });

    public StrategyDescriptor Descriptor { get; } = new StrategyDescriptor("Scripted", "1.0.0");

    public int WarmupBars => 0;

    public void Initialize(StrategyContext context) => _bar = 0;

    public Signal? OnBar(MarketSnapshot snapshot)
    {
        var index = _bar++;
        return _script.TryGetValue(index, out var action) ? action(snapshot) : null;
    }
}
