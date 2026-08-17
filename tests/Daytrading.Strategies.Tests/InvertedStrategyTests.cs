using System;
using Daytrading.Strategies;
using Daytrading.Strategies.Model;
using Daytrading.Strategies.Tests.TestSupport;

namespace Daytrading.Strategies.Tests;

/// <summary>
/// Die gespiegelte Strategie. Wichtig ist, dass wirklich <em>nur</em> die Richtung kippt:
/// Stopabstand und Chance-Risiko-Verhältnis müssen erhalten bleiben, sonst vergleicht man
/// hinterher zwei verschiedene Strategien statt zweier Richtungen.
/// </summary>
public class InvertedStrategyTests
{
    private sealed class FixedSignalStrategy : IStrategy
    {
        private readonly Signal? _signal;

        public FixedSignalStrategy(Signal? signal) => _signal = signal;

        public StrategyDescriptor Descriptor { get; } = new StrategyDescriptor("Fixed", "1.0.0");

        public int WarmupBars => 7;

        public bool Initialized { get; private set; }

        public void Initialize(StrategyContext context) => Initialized = true;

        public Signal? OnBar(MarketSnapshot snapshot) => _signal;
    }

    private static readonly DateTime Day = TestMarket.Utc(2024, 3, 4);

    private static MarketSnapshot Snapshot()
    {
        var series = new BarSeries();
        series.Append(new Candle(Day.AddHours(14), 100m, 101m, 99m, 100m, 1_000m));
        return new MarketSnapshot(
            TestMarket.Equity(),
            Timeframe.M5,
            series,
            TestMarket.UsEquitySession(Day),
            Array.Empty<Position>());
    }

    private static StrategyContext Context() =>
        new StrategyContext(TestMarket.Equity(), Timeframe.M5, StrategyParameters.Empty);

    [Fact]
    public void Turns_a_long_into_a_short_and_mirrors_stop_and_target()
    {
        var original = Signal.Entry(TradeDirection.Long, 100m, 99m, 102m, 0.5m, "Ausbruch");
        var inverted = new InvertedStrategy(new FixedSignalStrategy(original));

        var signal = inverted.OnBar(Snapshot())!;

        Assert.Equal(TradeDirection.Short, signal.Direction);
        Assert.Equal(100m, signal.ReferencePrice);
        Assert.Equal(101m, signal.StopLoss);      // aus 1 unter dem Einstieg wird 1 darüber
        Assert.Equal(98m, signal.TakeProfit);     // aus 2 über dem Einstieg wird 2 darunter
    }

    [Fact]
    public void Turns_a_short_into_a_long()
    {
        var original = Signal.Entry(TradeDirection.Short, 100m, 101.5m, 97m, null, "Ausbruch");
        var inverted = new InvertedStrategy(new FixedSignalStrategy(original));

        var signal = inverted.OnBar(Snapshot())!;

        Assert.Equal(TradeDirection.Long, signal.Direction);
        Assert.Equal(98.5m, signal.StopLoss);
        Assert.Equal(103m, signal.TakeProfit);
    }

    [Fact]
    public void Keeps_the_stop_distance_and_the_reward_to_risk_ratio()
    {
        // Sonst waere der Vergleich wertlos: Zwei Laeufe mit unterschiedlichem Stopabstand
        // unterscheiden sich nicht nur in der Richtung.
        var original = Signal.Entry(TradeDirection.Long, 250m, 247.5m, 255m, null, "x");
        var inverted = new InvertedStrategy(new FixedSignalStrategy(original)).OnBar(Snapshot())!;

        Assert.Equal(original.StopDistance, inverted.StopDistance);
        Assert.Equal(original.RewardRiskRatio, inverted.RewardRiskRatio);
    }

    [Fact]
    public void Leaves_an_exit_signal_untouched()
    {
        // "Schliessen" hat keine Gegenrichtung.
        var inverted = new InvertedStrategy(new FixedSignalStrategy(Signal.CloseAll(100m, "Rueckkehr")));

        var signal = inverted.OnBar(Snapshot())!;

        Assert.Equal(SignalKind.CloseAll, signal.Kind);
    }

    [Fact]
    public void Passes_initialisation_and_warmup_through_to_the_inner_strategy()
    {
        var inner = new FixedSignalStrategy(null);
        var inverted = new InvertedStrategy(inner);

        inverted.Initialize(Context());

        Assert.True(inner.Initialized);
        Assert.Equal(7, inverted.WarmupBars);
        Assert.Equal("InvertedFixed", inverted.Descriptor.Name);
    }

    [Fact]
    public void Is_available_from_the_catalog_under_its_own_name()
    {
        Assert.Contains("InvertedVwapReversion", StrategyCatalog.Names);
        Assert.Contains("InvertedOpeningRangeBreakout", StrategyCatalog.Names);
    }
}
