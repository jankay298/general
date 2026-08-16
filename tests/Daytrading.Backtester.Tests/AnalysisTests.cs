using Daytrading.Backtester.Tests.TestSupport;
using Daytrading.Execution;
using Daytrading.Strategies.Model;

namespace Daytrading.Backtester.Tests;

public class PerformanceMetricsTests
{
    private static TradeRecord Trade(int day, decimal netPnL, decimal risk = 100m, ExitReason reason = ExitReason.TakeProfit)
    {
        var entry = Fixture.Monday.AddDays(day).AddHours(10);
        var position = new Position(
            "T" + day, "TEST", TradeDirection.Long, entry, 100m, 99m, null, 10m, "S");

        return new TradeRecord("S", position, entry.AddMinutes(45), 101m, reason, netPnL, 0m, risk, 1m);
    }

    private static IReadOnlyList<EquityPoint> Curve(params decimal[] values)
    {
        var points = new List<EquityPoint>(values.Length);
        for (var i = 0; i < values.Length; i++)
        {
            points.Add(new EquityPoint(Fixture.Monday.AddDays(i).AddHours(17), values[i]));
        }

        return points;
    }

    [Fact]
    public void Sums_results_and_derives_the_basic_ratios()
    {
        var trades = new[] { Trade(0, 200m), Trade(1, -100m), Trade(2, 300m), Trade(3, -100m) };

        var metrics = PerformanceMetrics.Compute(trades, Curve(10_000m, 10_200m, 10_100m, 10_400m, 10_300m), 10_000m);

        Assert.Equal(4, metrics.TradeCount);
        Assert.Equal(300m, metrics.NetPnL);
        Assert.Equal(2, metrics.Wins);
        Assert.Equal(2, metrics.Losses);
        Assert.Equal(50m, metrics.HitRate);
        Assert.Equal(75m, metrics.ExpectancyPerTrade);
        Assert.Equal(2.5m, metrics.ProfitFactor);   // 500 Gewinn / 200 Verlust
    }

    [Fact]
    public void Expectancy_in_r_is_independent_of_the_account_size()
    {
        var trades = new[] { Trade(0, 200m, risk: 100m), Trade(1, -100m, risk: 100m) };

        var metrics = PerformanceMetrics.Compute(trades, Curve(10_000m, 10_200m, 10_100m), 10_000m);

        Assert.Equal(0.5m, metrics.ExpectancyR);   // (+2R -1R) / 2
    }

    [Fact]
    public void Max_drawdown_comes_from_the_equity_curve_not_from_the_trades()
    {
        var metrics = PerformanceMetrics.Compute(
            new[] { Trade(0, 100m) }, Curve(10_000m, 11_000m, 9_900m, 10_500m), 10_000m);

        Assert.Equal(1_100m, metrics.MaxDrawdownAmount);
        Assert.Equal(10m, metrics.MaxDrawdownPercent);
    }

    [Fact]
    public void Counts_the_longest_losing_streak()
    {
        var trades = new[]
        {
            Trade(0, 100m), Trade(1, -50m), Trade(2, -50m), Trade(3, -50m), Trade(4, 100m), Trade(5, -50m),
        };

        var metrics = PerformanceMetrics.Compute(trades, Curve(10_000m, 10_100m), 10_000m);

        Assert.Equal(3, metrics.LongestLosingStreak);
    }

    [Fact]
    public void Flags_results_with_too_few_trades_and_estimates_the_waiting_time()
    {
        var trades = new[] { Trade(0, 10m), Trade(7, 10m) };   // zwei Trades in einer Woche

        var metrics = PerformanceMetrics.Compute(trades, Curve(10_000m, 10_010m, 10_020m), 10_000m, minimumSampleTrades: 30);

        Assert.True(metrics.IsStatisticallyWeak);
        Assert.NotNull(metrics.WeeksToMinimumSample);
        Assert.InRange(metrics.WeeksToMinimumSample!.Value, 10m, 30m);
    }

    [Fact]
    public void A_run_without_trades_is_weak_by_definition()
    {
        var metrics = PerformanceMetrics.Compute(Array.Empty<TradeRecord>(), Curve(10_000m), 10_000m);

        Assert.Equal(0, metrics.TradeCount);
        Assert.True(metrics.IsStatisticallyWeak);
    }

    [Fact]
    public void Profit_factor_is_undefined_rather_than_infinite_without_losses()
    {
        var metrics = PerformanceMetrics.Compute(
            new[] { Trade(0, 100m), Trade(1, 50m) }, Curve(10_000m, 10_100m, 10_150m), 10_000m);

        Assert.Null(metrics.ProfitFactor);
    }
}

public class RegimeAnalysisTests
{
    private static List<Candle> Day(DateTime day, decimal open, decimal high, decimal low, decimal close)
    {
        return new List<Candle>
        {
            new Candle(day.AddHours(9), open, high, low, close, 100m),
        };
    }

    [Fact]
    public void Splits_days_into_volatility_terciles()
    {
        var bars = new List<Candle>();
        bars.AddRange(Day(Fixture.Monday, 100m, 100.5m, 99.5m, 100m));          // 1 % Spanne
        bars.AddRange(Day(Fixture.Monday.AddDays(1), 100m, 102m, 98m, 100m));   // 4 %
        bars.AddRange(Day(Fixture.Monday.AddDays(2), 100m, 110m, 90m, 100m));   // 20 %

        var regimes = RegimeAnalysis.TagDays(bars);

        Assert.Equal(VolatilityRegime.Low, regimes[Fixture.Monday.Date].Volatility);
        Assert.Equal(VolatilityRegime.High, regimes[Fixture.Monday.AddDays(2).Date].Volatility);
    }

    [Fact]
    public void Recognises_trend_direction_and_sideways_days()
    {
        var bars = new List<Candle>();
        bars.AddRange(Day(Fixture.Monday, 100m, 106m, 99m, 105m));              // klar aufwärts
        bars.AddRange(Day(Fixture.Monday.AddDays(1), 100m, 101m, 94m, 95m));    // klar abwärts
        bars.AddRange(Day(Fixture.Monday.AddDays(2), 100m, 100.5m, 99.5m, 100m));

        var regimes = RegimeAnalysis.TagDays(bars);

        Assert.Equal(TrendRegime.Up, regimes[Fixture.Monday.Date].Trend);
        Assert.Equal(TrendRegime.Down, regimes[Fixture.Monday.AddDays(1).Date].Trend);
        Assert.Equal(TrendRegime.Sideways, regimes[Fixture.Monday.AddDays(2).Date].Trend);
    }

    [Fact]
    public void Drawdown_in_r_follows_the_cumulative_r_curve()
    {
        var trades = new[]
        {
            MakeTrade(0, 2m), MakeTrade(1, -1m), MakeTrade(2, -1m), MakeTrade(3, -1m), MakeTrade(4, 1m),
        };

        // Kumuliert: 2, 1, 0, -1, 0 -> Hoch bei 2, Tief bei -1 -> 3 R.
        Assert.Equal(3m, RegimeAnalysis.MaxDrawdownR(trades));
    }

    private static TradeRecord MakeTrade(int day, decimal rMultiple)
    {
        var entry = Fixture.Monday.AddDays(day).AddHours(10);
        var position = new Position("T" + day, "TEST", TradeDirection.Long, entry, 100m, 99m, null, 10m, "S");
        return new TradeRecord("S", position, entry.AddMinutes(30), 100m, ExitReason.StopLoss, rMultiple * 100m, 0m, 100m, 1m);
    }
}

public class WalkForwardAndRegistryTests
{
    [Fact]
    public void The_parameter_grid_is_the_cross_product_of_its_dimensions()
    {
        var grid = StrategyRegistry.Grid(
            ("A", new[] { "1", "2", "3" }),
            ("B", new[] { "x", "y" }));

        Assert.Equal(6, grid.Count);
        Assert.All(grid, combination => Assert.Equal(2, combination.Count));
        Assert.Contains(grid, combination => combination["A"] == "3" && combination["B"] == "y");
    }

    [Fact]
    public void Every_registered_strategy_can_be_created_and_carries_a_grid()
    {
        Assert.NotEmpty(StrategyRegistry.All);

        foreach (var registration in StrategyRegistry.All)
        {
            var strategy = registration.Factory();
            Assert.NotNull(strategy);
            Assert.NotEmpty(registration.ParameterGrid);
        }
    }

    [Fact]
    public void Unknown_strategies_are_reported_with_the_known_ones()
    {
        var error = Assert.Throws<ArgumentException>(() => StrategyRegistry.Get("GibtsNicht"));

        Assert.Contains("OpeningRangeBreakout", error.Message);
    }

    [Fact]
    public void The_train_test_split_keeps_the_order_and_never_overlaps()
    {
        var bars = Fixture.FlatSession(Fixture.Monday, 10);

        var (train, test) = WalkForwardRunner.Split(bars, 0.7m);

        Assert.Equal(7, train.Count);
        Assert.Equal(3, test.Count);
        Assert.True(train[^1].OpenTimeUtc < test[0].OpenTimeUtc);
    }

    [Fact]
    public void Walk_forward_rolls_windows_and_builds_the_profile_only_from_out_of_sample_results()
    {
        var config = Fixture.Symbol();
        var bars = new List<Candle>();
        for (var day = 0; day < 200; day++)
        {
            var date = Fixture.Monday.AddDays(day);
            if (date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
            {
                continue;
            }

            bars.AddRange(Fixture.FlatSession(date, 32, 100m + day % 7));
        }

        var registration = new StrategyRegistration(
            "Scripted",
            () => ScriptedStrategy.EntryAt(0, TradeDirection.Long, stop: 95m),
            StrategyRegistry.Grid(("Dummy", new[] { "1", "2" })));

        var report = new WalkForwardRunner(RiskLimits.Default, Fixture.Options())
            .Run(new SymbolData(config, bars), registration, trainDays: 60, testDays: 30);

        Assert.NotEmpty(report.Windows);
        Assert.All(report.Windows, window => Assert.True(window.TrainToUtc <= window.TestFromUtc));
        Assert.True(report.CombinationsTested >= report.Windows.Count * 3);

        if (report.Profile != null)
        {
            Assert.Equal(report.Windows.Count(window => window.TestResult?.Metrics.TradeCount > 0), report.Profile.WindowCount);
        }
    }

    [Fact]
    public void A_period_too_short_for_a_single_window_says_so()
    {
        var config = Fixture.Symbol();
        var bars = Fixture.FlatSession(Fixture.Monday, 32);

        var report = new WalkForwardRunner(RiskLimits.Default, Fixture.Options())
            .Run(new SymbolData(config, bars), StrategyRegistry.All[0], trainDays: 180, testDays: 60);

        Assert.Empty(report.Windows);
        Assert.Contains(report.Warnings, warning => warning.Contains("reicht nicht"));
    }
}

public class SyntheticMarketTests
{
    [Fact]
    public void The_same_seed_produces_the_same_market()
    {
        var config = Fixture.Symbol();
        var from = Fixture.Monday;
        var to = Fixture.Monday.AddDays(10);

        var first = SyntheticMarket.Generate(config, from, to, seed: 42);
        var second = SyntheticMarket.Generate(config, from, to, seed: 42);
        var different = SyntheticMarket.Generate(config, from, to, seed: 43);

        Assert.Equal(first.Count, second.Count);
        Assert.Equal(first.Select(bar => bar.Close), second.Select(bar => bar.Close));
        Assert.NotEqual(first.Select(bar => bar.Close), different.Select(bar => bar.Close));
    }

    [Fact]
    public void Generated_bars_stay_inside_the_session_and_on_the_tick_grid()
    {
        var config = Fixture.Symbol();
        var bars = SyntheticMarket.Generate(config, Fixture.Monday, Fixture.Monday.AddDays(5), seed: 7);

        Assert.NotEmpty(bars);
        Assert.All(bars, bar =>
        {
            Assert.InRange(bar.OpenTimeUtc.TimeOfDay, TimeSpan.FromHours(9), TimeSpan.FromHours(17));
            Assert.Equal(0m, bar.Close % config.TickSize);
            Assert.True(bar.High >= bar.Low);
        });
    }
}
