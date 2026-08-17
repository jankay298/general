using Daytrading.Backtester.Tests.TestSupport;
using Daytrading.Execution;
using Daytrading.Strategies;
using Daytrading.Strategies.Model;

namespace Daytrading.Backtester.Tests;

/// <summary>
/// Verhalten am Gesamt-Drawdown. Diese Tests sind entstanden, nachdem ein Vierjahreslauf auf
/// echten Daten zeigte, dass ein Backtest nach Erreichen der Grenze noch jahrelang weiterlief -
/// ohne einen einzigen Trade, aber mit vollem Gewicht in Sharpe, Drawdown und Trades pro Woche.
/// </summary>
public class DrawdownStopTests
{
    /// <summary>Strategie, die auf jeder Bar einen Einstieg versucht - so ist der Drawdown schnell erreicht.</summary>
    private sealed class AlwaysEnterStrategy : IStrategy
    {
        public StrategyDescriptor Descriptor { get; } = new StrategyDescriptor("AlwaysEnter", "1.0.0");

        public int WarmupBars => 0;

        public void Initialize(StrategyContext context)
        {
        }

        public Signal? OnBar(MarketSnapshot snapshot)
        {
            if (snapshot.HasOpenPosition)
            {
                return null;
            }

            var close = snapshot.Current.Close;
            return Signal.Entry(TradeDirection.Long, close, close - 1m, null, null, "immer");
        }
    }

    /// <summary>Fallende Kurse über viele Tage: Jeder Long-Einstieg läuft in den Stop.</summary>
    private static List<Candle> FallingMarket(int days)
    {
        var bars = new List<Candle>();
        var price = 500m;

        for (var day = 0; day < days; day++)
        {
            var date = Fixture.Monday.AddDays(day);
            if (date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
            {
                continue;
            }

            for (var i = 0; i < 32; i++)
            {
                var open = price;
                var close = price - 0.5m;
                bars.Add(new Candle(date.AddHours(9).AddMinutes(15 * i), open, open + 0.1m, close - 0.5m, close, 1_000m));
                price = close;
            }
        }

        return bars;
    }

    private static BacktestResult Run(IReadOnlyList<Candle> bars, RiskLimits? limits = null) =>
        new BacktestRunner().Run(new BacktestJob(
            new AlwaysEnterStrategy(),
            StrategyParameters.Empty,
            Fixture.Symbol(),
            bars,
            limits ?? RiskLimits.Default,
            Fixture.Options()));

    [Fact]
    public void A_run_ends_when_the_total_drawdown_limit_is_reached()
    {
        var bars = FallingMarket(120);

        var result = Run(bars);

        Assert.NotNull(result.StoppedAtUtc);
        Assert.Contains("Gesamt-Drawdown", result.StopReason);
        Assert.True(
            result.StoppedAtUtc!.Value < bars[^1].OpenTimeUtc,
            "Der Lauf muss vor dem Datenende enden, sonst wäre die Grenze wirkungslos.");
    }

    [Fact]
    public void The_metrics_of_a_stopped_run_cover_only_its_active_period()
    {
        var bars = FallingMarket(120);

        var result = Run(bars);

        // Ohne den Abbruch liefen hier Monate ohne Trades in Sharpe und Drawdown ein.
        var totalDays = (bars[^1].OpenTimeUtc - bars[0].OpenTimeUtc).TotalDays;
        Assert.True(result.ActiveDays < totalDays);
        Assert.Equal(result.EquityCurve[^1].TimeUtc.Date, result.StoppedAtUtc!.Value.Date);
    }

    [Fact]
    public void Open_positions_of_a_stopped_run_are_closed_with_the_risk_reason()
    {
        var result = Run(FallingMarket(120));

        Assert.All(result.Trades, trade => Assert.NotEqual(ExitReason.EndOfData, trade.ExitReason));
    }

    [Fact]
    public void The_drawdown_is_measured_from_the_starting_balance_not_from_the_first_bar()
    {
        // Der erste Punkt der Kurve entsteht erst nach der ersten Bar und kann schon im Minus
        // liegen. Wird von dort aus gemessen, verschwindet der Rueckgang davor - und zwar immer
        // zugunsten des Ergebnisses.
        var curve = new List<EquityPoint>
        {
            new EquityPoint(Fixture.Monday.AddHours(10), 9_500m),   // schon 5 % unter Start
            new EquityPoint(Fixture.Monday.AddHours(11), 9_000m),
            new EquityPoint(Fixture.Monday.AddHours(12), 9_800m),
        };

        var entry = Fixture.Monday.AddHours(10);
        var position = new Position("T1", "TEST", TradeDirection.Long, entry, 100m, 99m, null, 10m, "S");
        var trade = new TradeRecord(
            "S", position, entry.AddMinutes(45), 99m, ExitReason.StopLoss, -1_000m, 0m, 100m, 1m);

        var metrics = PerformanceMetrics.Compute(new[] { trade }, curve, startingBalance: 10_000m);

        Assert.Equal(10m, metrics.MaxDrawdownPercent, 2);
        Assert.Equal(1_000m, metrics.MaxDrawdownAmount);
    }

    [Fact]
    public void A_stopped_run_reports_a_drawdown_at_least_as_large_as_the_limit()
    {
        // Sonst widerspricht der Bericht sich selbst: "wegen 10 % Drawdown beendet" neben einer
        // ausgewiesenen Spitze von 8.6 % laedt dazu ein, eine der beiden Zahlen zu glauben.
        var result = Run(FallingMarket(120));

        Assert.NotNull(result.StoppedAtUtc);
        Assert.True(
            result.Metrics.MaxDrawdownPercent >= RiskLimits.Default.MaxTotalDrawdownPercent,
            $"Lauf wegen Gesamt-Drawdown beendet, ausgewiesen sind aber nur " +
            $"{result.Metrics.MaxDrawdownPercent:0.00} % statt mindestens " +
            $"{RiskLimits.Default.MaxTotalDrawdownPercent} %.");
    }

    [Fact]
    public void A_run_that_stays_within_the_limit_is_not_marked_as_stopped()
    {
        var result = Run(Fixture.FlatSession(Fixture.Monday, 32));

        Assert.Null(result.StoppedAtUtc);
        Assert.Null(result.StopReason);
    }

    [Fact]
    public void A_wider_limit_lets_the_run_go_further()
    {
        var bars = FallingMarket(120);
        var generous = RiskLimits.Default;
        generous.MaxTotalDrawdownPercent = 30m;

        var strict = Run(bars);
        var wide = Run(bars, generous);

        Assert.True(wide.ActiveDays > strict.ActiveDays);
        Assert.True(wide.Metrics.TradeCount > strict.Metrics.TradeCount);
    }
}
