using System;
using System.Collections.Generic;
using Daytrading.Strategies;
using Daytrading.Strategies.Model;
using Daytrading.Strategies.Tests.TestSupport;

namespace Daytrading.Strategies.Tests.Model;

public class MarketSnapshotTests
{
    /// <summary>
    /// Protokolliert, was die Strategie je Bar tatsächlich sehen konnte. Damit wird der
    /// wichtigste Bias des Backtestings direkt geprüft und nicht nur behauptet.
    /// </summary>
    private sealed class HistoryProbeStrategy : IStrategy
    {
        public List<int> HistoryCounts { get; } = new List<int>();

        public List<DateTime> NewestBarTimes { get; } = new List<DateTime>();

        public List<DateTime> DecisionTimes { get; } = new List<DateTime>();

        public List<bool> CouldSeeOneBarFurther { get; } = new List<bool>();

        public StrategyDescriptor Descriptor { get; } = new StrategyDescriptor("HistoryProbe", "1.0.0");

        public int WarmupBars => 0;

        public void Initialize(StrategyContext context)
        {
        }

        public Signal? OnBar(MarketSnapshot snapshot)
        {
            HistoryCounts.Add(snapshot.History.Count);
            NewestBarTimes.Add(snapshot.History.Last().OpenTimeUtc);
            DecisionTimes.Add(snapshot.BarCloseTimeUtc);
            CouldSeeOneBarFurther.Add(snapshot.History.TryLast(snapshot.History.Count, out _));
            return null;
        }
    }

    [Fact]
    public void Strategy_sees_history_only_up_to_and_including_the_current_bar()
    {
        var probe = new HistoryProbeStrategy();
        var start = TestMarket.Utc(2024, 3, 1, 14, 0);
        var bars = TestMarket.Series(
            start,
            Timeframe.M15,
            (101m, 99m, 100m),
            (102m, 100m, 101m),
            (103m, 101m, 102m));

        var harness = new StrategyHarness(probe, TestMarket.Equity(), Timeframe.M15, TestMarket.UsEquitySession);
        harness.Run(bars);

        Assert.Equal(new[] { 1, 2, 3 }, probe.HistoryCounts);
        Assert.Equal(new[] { bars[0].OpenTimeUtc, bars[1].OpenTimeUtc, bars[2].OpenTimeUtc }, probe.NewestBarTimes);
        Assert.All(probe.CouldSeeOneBarFurther, canSee => Assert.False(canSee));
    }

    [Fact]
    public void Decision_time_is_the_close_of_the_bar_not_its_open()
    {
        var probe = new HistoryProbeStrategy();
        var start = TestMarket.Utc(2024, 3, 1, 14, 0);
        var bars = TestMarket.Series(start, Timeframe.M15, (101m, 99m, 100m), (102m, 100m, 101m));

        var harness = new StrategyHarness(probe, TestMarket.Equity(), Timeframe.M15, TestMarket.UsEquitySession);
        harness.Run(bars);

        Assert.Equal(
            new[] { start.AddMinutes(15), start.AddMinutes(30) },
            probe.DecisionTimes);
    }

    [Fact]
    public void Exposes_session_context_relative_to_the_bar_close()
    {
        var series = new BarSeries();
        series.Append(TestMarket.Bar(TestMarket.Utc(2024, 3, 1, 19, 30), 100m));
        var session = TestMarket.UsEquitySession(TestMarket.Utc(2024, 3, 1));

        var snapshot = new MarketSnapshot(TestMarket.Equity(), Timeframe.M15, series, session);

        Assert.True(snapshot.IsInSession);
        Assert.Equal(TimeSpan.FromMinutes(15), snapshot.TimeUntilSessionEnd);
        Assert.Equal(TimeSpan.FromHours(6.25), snapshot.TimeSinceSessionStart);
        Assert.False(snapshot.HasOpenPosition);
    }

    [Fact]
    public void Rejects_an_empty_history()
    {
        var session = TestMarket.UsEquitySession(TestMarket.Utc(2024, 3, 1));

        Assert.Throws<ArgumentException>(
            () => new MarketSnapshot(TestMarket.Equity(), Timeframe.M15, new BarSeries(), session));
    }
}
