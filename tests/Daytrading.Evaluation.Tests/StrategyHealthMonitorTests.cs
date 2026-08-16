using Daytrading.Evaluation;

namespace Daytrading.Evaluation.Tests;

public class StatisticTests
{
    [Fact]
    public void Computes_mean_deviation_and_bands()
    {
        var statistic = Statistic.FromSamples(new[] { 1m, 2m, 3m, 4m, 5m });

        Assert.Equal(3m, statistic.Mean);
        Assert.Equal(1m, statistic.Minimum);
        Assert.Equal(5m, statistic.Maximum);
        Assert.Equal(5, statistic.SampleCount);
        Assert.InRange(statistic.StandardDeviation, 1.5m, 1.6m);   // Stichproben-Standardabweichung ~1.58
        Assert.True(statistic.LowerBand(2m) < statistic.Mean);
        Assert.True(statistic.UpperBand(2m) > statistic.Mean);
    }

    [Fact]
    public void A_single_sample_has_no_spread()
    {
        var statistic = Statistic.FromSamples(new[] { 42m });

        Assert.Equal(42m, statistic.Mean);
        Assert.Equal(0m, statistic.StandardDeviation);
    }

    [Fact]
    public void No_samples_produce_zeros_instead_of_an_exception()
    {
        var statistic = Statistic.FromSamples(Array.Empty<decimal>());

        Assert.Equal(0, statistic.SampleCount);
        Assert.Equal(0m, statistic.Mean);
    }
}

public class ExpectationProfileTests
{
    [Fact]
    public void A_profile_from_too_few_windows_is_not_a_yardstick()
    {
        var thin = Fixture.Profile(windowCount: 2, totalTrades: 20);
        var solid = Fixture.Profile(windowCount: 6, totalTrades: 200);

        Assert.False(thin.IsTrustworthy());
        Assert.True(solid.IsTrustworthy());
    }
}

internal static class Fixture
{
    public static readonly DateTime Start = new DateTime(2024, 3, 1, 12, 0, 0, DateTimeKind.Utc);

    public static ExpectationProfile Profile(
        decimal expectancyMean = 0.2m,
        decimal expectancyDeviation = 0.1m,
        decimal worstDrawdownR = 10m,
        int longestLosingStreak = 30,
        decimal tradesPerWeek = 5m,
        decimal assumedSpread = 0.5m,
        int windowCount = 6,
        int totalTrades = 200) =>
        new ExpectationProfile(
            "TestStrategy",
            "TESTSYM",
            Start,
            windowCount,
            totalTrades,
            new Statistic(expectancyMean, expectancyDeviation, expectancyMean - 0.3m, expectancyMean + 0.3m, windowCount),
            new Statistic(45m, 5m, 35m, 55m, windowCount),
            new Statistic(tradesPerWeek, 1m, tradesPerWeek - 2m, tradesPerWeek + 2m, windowCount),
            new Statistic(90m, 20m, 40m, 150m, windowCount),
            worstDrawdownR,
            longestLosingStreak,
            assumedSpread);

    public static TradeObservation Trade(int index, decimal rMultiple, decimal spread = 0m) =>
        new TradeObservation(Start.AddDays(index), rMultiple, spread);
}

public class StrategyHealthMonitorTests
{
    [Fact]
    public void Starts_healthy_with_full_risk()
    {
        var monitor = new StrategyHealthMonitor(Fixture.Profile());

        Assert.Equal(HealthState.Healthy, monitor.State);
        Assert.Equal(1m, monitor.RiskMultiplier);
    }

    [Fact]
    public void Stays_healthy_while_results_match_the_profile()
    {
        var monitor = new StrategyHealthMonitor(Fixture.Profile());

        for (var i = 0; i < 40; i++)
        {
            monitor.Observe(Fixture.Trade(i, i % 3 == 0 ? 1.2m : -0.3m));
        }

        Assert.Equal(HealthState.Healthy, monitor.State);
        Assert.Empty(monitor.History);
    }

    [Fact]
    public void Five_bad_trades_do_not_switch_anything_off()
    {
        // Die wichtigste Regel der Schicht: erst Stichprobe, dann Entscheidung.
        var monitor = new StrategyHealthMonitor(Fixture.Profile(worstDrawdownR: 3m, longestLosingStreak: 4));

        HealthAssessment? assessment = null;
        for (var i = 0; i < 5; i++)
        {
            assessment = monitor.Observe(Fixture.Trade(i, -1m));
        }

        Assert.NotEqual(HealthState.Suspended, monitor.State);
        Assert.Equal(HealthState.Watch, monitor.State);
        Assert.Contains(assessment!.Findings, finding => finding.Contains("Noch keine Entscheidung"));
    }

    [Fact]
    public void Suspends_when_the_drawdown_passes_the_worst_ever_seen_and_the_sample_is_large_enough()
    {
        var monitor = new StrategyHealthMonitor(Fixture.Profile(worstDrawdownR: 3m, longestLosingStreak: 100));

        for (var i = 0; i < 30; i++)
        {
            monitor.Observe(Fixture.Trade(i, -0.2m));
        }

        Assert.Equal(HealthState.Suspended, monitor.State);
        Assert.Equal(0m, monitor.RiskMultiplier);
        Assert.Contains(monitor.History, change => change.Trigger.Contains("Drawdown"));
    }

    [Fact]
    public void Suspends_on_a_losing_streak_longer_than_any_in_the_backtest()
    {
        var monitor = new StrategyHealthMonitor(Fixture.Profile(worstDrawdownR: 1000m, longestLosingStreak: 5));

        for (var i = 0; i < 30; i++)
        {
            monitor.Observe(Fixture.Trade(i, -0.01m));
        }

        Assert.Equal(HealthState.Suspended, monitor.State);
        Assert.Contains(monitor.History, change => change.Trigger.Contains("Verlustserie"));
    }

    [Fact]
    public void Halves_the_risk_when_expectancy_drops_below_the_confidence_band()
    {
        var monitor = new StrategyHealthMonitor(
            Fixture.Profile(expectancyMean: 0.2m, expectancyDeviation: 0.1m, worstDrawdownR: 1000m, longestLosingStreak: 1000));

        for (var i = 0; i < 20; i++)
        {
            monitor.Observe(Fixture.Trade(i, -0.1m));
        }

        Assert.Equal(HealthState.Degraded, monitor.State);
        Assert.Equal(0.5m, monitor.RiskMultiplier);
    }

    [Fact]
    public void An_unusual_trade_frequency_is_a_hint_not_a_verdict()
    {
        var monitor = new StrategyHealthMonitor(
            Fixture.Profile(tradesPerWeek: 5m, worstDrawdownR: 1000m, longestLosingStreak: 1000));

        HealthAssessment? assessment = null;
        for (var i = 0; i < 25; i++)
        {
            // 25 Trades in gut acht Tagen - rund 21 statt der erwarteten 5 je Woche.
            assessment = monitor.Observe(new TradeObservation(Fixture.Start.AddHours(i * 8), 0.5m));
        }

        Assert.Equal(HealthState.Watch, monitor.State);
        Assert.Contains(assessment!.Findings, finding => finding.Contains("Regimewechsel oder einen Fehler"));
    }

    [Fact]
    public void A_wider_spread_than_assumed_is_flagged_as_an_execution_problem()
    {
        // Die Unterscheidung, auf die es ankommt: Der Broker ist teurer als angenommen -
        // das ist kein Grund, die Strategie abzuschalten.
        var monitor = new StrategyHealthMonitor(
            Fixture.Profile(assumedSpread: 0.5m, worstDrawdownR: 1000m, longestLosingStreak: 1000));

        HealthAssessment? assessment = null;
        for (var i = 0; i < 10; i++)
        {
            assessment = monitor.Observe(Fixture.Trade(i, 0.3m, spread: 1.2m));
        }

        Assert.True(assessment!.ExecutionProblem);
        Assert.Contains(assessment.Findings, finding => finding.Contains("AUSFÜHRUNGSPROBLEM"));
        Assert.Equal(HealthState.Healthy, monitor.State);
    }

    [Fact]
    public void Every_state_change_is_logged_with_time_trigger_and_data_basis()
    {
        var monitor = new StrategyHealthMonitor(Fixture.Profile(worstDrawdownR: 2m, longestLosingStreak: 1000));

        for (var i = 0; i < 30; i++)
        {
            monitor.Observe(Fixture.Trade(i, -0.2m));
        }

        var change = monitor.History.Last();
        Assert.Equal(HealthState.Suspended, change.To);
        Assert.NotEqual(default, change.TimestampUtc);
        Assert.False(string.IsNullOrWhiteSpace(change.Trigger));
        Assert.Contains("Trades", change.DataBasis);
    }

    [Fact]
    public void A_suspended_combination_does_not_come_back_on_its_own()
    {
        var monitor = Suspended();

        // Auch gute Trades holen sie nicht zurück - es gibt schlicht keine neuen mehr.
        var assessment = monitor.Evaluate(Fixture.Start.AddDays(365));

        Assert.Equal(HealthState.Suspended, assessment.State);
        Assert.Contains(assessment.Findings, finding => finding.Contains("Out-of-Sample"));
    }

    [Fact]
    public void Revalidation_before_the_cooldown_has_passed_is_refused()
    {
        var monitor = Suspended();

        monitor.SubmitRevalidation(passed: true, Fixture.Start.AddDays(1), "frischer Test");

        Assert.Equal(HealthState.Suspended, monitor.State);
        Assert.Contains(monitor.History, change => change.Trigger.Contains("Abkühlphase"));
    }

    [Fact]
    public void A_failed_revalidation_keeps_the_combination_switched_off()
    {
        var monitor = Suspended();

        monitor.SubmitRevalidation(passed: false, Fixture.Start.AddDays(100), "Test nicht bestanden");

        Assert.Equal(HealthState.Suspended, monitor.State);
    }

    [Fact]
    public void A_passed_revalidation_after_the_cooldown_reactivates_under_observation()
    {
        var monitor = Suspended();

        monitor.SubmitRevalidation(passed: true, Fixture.Start.AddDays(100), "Out-of-Sample 2024-Q3 bestanden");

        // Zurück, aber unter Beobachtung und mit frischer Zählung - nicht sofort auf Healthy.
        Assert.Equal(HealthState.Watch, monitor.State);
        Assert.Equal(1m, monitor.RiskMultiplier);
        Assert.Equal(0, monitor.TradeCount);
        Assert.Contains(monitor.History, change => change.Trigger.Contains("bestanden"));
    }

    private static StrategyHealthMonitor Suspended()
    {
        var monitor = new StrategyHealthMonitor(Fixture.Profile(worstDrawdownR: 2m, longestLosingStreak: 1000));
        for (var i = 0; i < 30; i++)
        {
            monitor.Observe(Fixture.Trade(i, -0.2m));
        }

        Assert.Equal(HealthState.Suspended, monitor.State);
        return monitor;
    }
}
