using Daytrading.Backtester.Tests.TestSupport;
using Daytrading.Execution;
using Daytrading.Strategies;
using Daytrading.Strategies.Model;

namespace Daytrading.Backtester.Tests;

public class CostModelTests
{
    [Fact]
    public void Entry_and_exit_are_always_worse_than_the_mid_price()
    {
        var cost = new CostModel(Fixture.Symbol(spread: 0.02m), slippageTicks: 1m);

        // Halber Spread 0.01 plus 1 Tick Slippage 0.01.
        Assert.Equal(100.02m, cost.EntryPrice(TradeDirection.Long, 100m, thinLiquidity: false));
        Assert.Equal(99.98m, cost.ExitPrice(TradeDirection.Long, 100m, thinLiquidity: false));
        Assert.Equal(99.98m, cost.EntryPrice(TradeDirection.Short, 100m, thinLiquidity: false));
        Assert.Equal(100.02m, cost.ExitPrice(TradeDirection.Short, 100m, thinLiquidity: false));
    }

    [Fact]
    public void A_round_trip_costs_at_least_one_full_spread()
    {
        var cost = new CostModel(Fixture.Symbol(spread: 0.10m), slippageTicks: 0m);

        var entry = cost.EntryPrice(TradeDirection.Long, 100m, false);
        var exit = cost.ExitPrice(TradeDirection.Long, 100m, false);

        Assert.Equal(0.10m, entry - exit);
    }

    [Fact]
    public void Stops_slip_more_than_market_orders_and_limits_not_at_all()
    {
        var cost = new CostModel(Fixture.Symbol(spread: 0m), slippageTicks: 1m, stopSlippageTicks: 3m);

        Assert.Equal(99.99m, cost.ExitPrice(TradeDirection.Long, 100m, false, FillKind.Market));
        Assert.Equal(99.97m, cost.ExitPrice(TradeDirection.Long, 100m, false, FillKind.Stop));
        Assert.Equal(100m, cost.ExitPrice(TradeDirection.Long, 100m, false, FillKind.Limit));
    }

    [Fact]
    public void Thin_liquidity_widens_the_spread()
    {
        var config = Fixture.Symbol(spread: 0.10m);
        config.ThinLiquiditySpreadFactor = 3m;
        var cost = new CostModel(config, slippageTicks: 0m);

        Assert.Equal(0.05m, cost.HalfSpread(thinLiquidity: false));
        Assert.Equal(0.15m, cost.HalfSpread(thinLiquidity: true));
    }

    [Fact]
    public void The_edges_of_the_session_count_as_thin()
    {
        var day = Fixture.Monday;
        var session = new TradingSession(day, day.AddHours(9), day.AddHours(17));   // 8 Stunden, Rand = 48 Minuten

        Assert.True(CostModel.IsThinLiquidity(session, day.AddHours(9).AddMinutes(30)));
        Assert.False(CostModel.IsThinLiquidity(session, day.AddHours(13)));
        Assert.True(CostModel.IsThinLiquidity(session, day.AddHours(16).AddMinutes(30)));
    }
}

public class BacktestRunnerTests
{
    private static BacktestResult Run(
        IStrategy strategy,
        IReadOnlyList<Candle> bars,
        RiskLimits? limits = null,
        BacktestOptions? options = null,
        Daytrading.Data.Config.SymbolConfig? symbol = null) =>
        new BacktestRunner().Run(new BacktestJob(
            strategy,
            StrategyParameters.Empty,
            symbol ?? Fixture.Symbol(),
            bars,
            limits ?? RiskLimits.Default,
            options ?? Fixture.Options()));

    [Fact]
    public void An_entry_is_filled_on_the_open_of_the_next_bar_not_on_the_signal_close()
    {
        // Signal auf Bar 2 (Close 100), Ausführung auf der Open von Bar 3 (101).
        var bars = Fixture.Session(
            Fixture.Monday,
            (100m, 100.5m, 99.5m, 100m),
            (100m, 100.5m, 99.5m, 100m),
            (100m, 100.5m, 99.5m, 100m),
            (101m, 101.5m, 100.5m, 101m),
            (101m, 101.5m, 100.5m, 101m));

        var result = Run(ScriptedStrategy.EntryAt(2, TradeDirection.Long, stop: 98m), bars);

        var trade = Assert.Single(result.Trades);
        Assert.Equal(bars[3].OpenTimeUtc, trade.EntryTimeUtc);
        Assert.Equal(101.02m, trade.EntryPrice);   // 101 + halber Spread 0.01 + 1 Tick Slippage
    }

    [Fact]
    public void A_stop_ends_the_trade_within_the_bar()
    {
        var bars = Fixture.Session(
            Fixture.Monday,
            (100m, 100.5m, 99.5m, 100m),
            (100m, 100.5m, 99.5m, 100m),
            (100m, 100.2m, 97.5m, 98m),     // Stop bei 99 wird durchschlagen
            (98m, 98.5m, 97.5m, 98m));

        var result = Run(ScriptedStrategy.EntryAt(0, TradeDirection.Long, stop: 99m), bars);

        var trade = Assert.Single(result.Trades);
        Assert.Equal(ExitReason.StopLoss, trade.ExitReason);
        Assert.Equal(98.96m, trade.ExitPrice);   // 99 - halber Spread 0.01 - 3 Ticks Stop-Slippage
        Assert.True(trade.NetPnL < 0m);
    }

    [Fact]
    public void A_target_ends_the_trade_without_negative_slippage()
    {
        var bars = Fixture.Session(
            Fixture.Monday,
            (100m, 100.5m, 99.5m, 100m),
            (100m, 100.5m, 99.5m, 100m),
            (100m, 103m, 99.9m, 102m),
            (102m, 102.5m, 101.5m, 102m));

        var result = Run(ScriptedStrategy.EntryAt(0, TradeDirection.Long, stop: 99m, takeProfit: 102m), bars);

        var trade = Assert.Single(result.Trades);
        Assert.Equal(ExitReason.TakeProfit, trade.ExitReason);
        Assert.Equal(101.99m, trade.ExitPrice);   // Limit: nur halber Spread, keine Slippage
        Assert.True(trade.NetPnL > 0m);
    }

    [Fact]
    public void When_stop_and_target_fall_into_the_same_bar_the_stop_counts()
    {
        // Die Bar berührt beides. Der Verlauf innerhalb der Bar ist unbekannt, also gilt die
        // pessimistische Annahme.
        var bars = Fixture.Session(
            Fixture.Monday,
            (100m, 100.5m, 99.5m, 100m),
            (100m, 100.5m, 99.5m, 100m),
            (100m, 103m, 97m, 100m),
            (100m, 100.5m, 99.5m, 100m));

        var result = Run(ScriptedStrategy.EntryAt(0, TradeDirection.Long, stop: 99m, takeProfit: 102m), bars);

        var trade = Assert.Single(result.Trades);
        Assert.Equal(ExitReason.StopLoss, trade.ExitReason);
    }

    [Fact]
    public void A_gap_past_the_stop_is_filled_at_the_opening_price_not_at_the_stop()
    {
        // Ein Stop ist keine Garantie: Springt der Kurs darüber hinweg, wird es teurer.
        var bars = Fixture.Session(
            Fixture.Monday,
            (100m, 100.5m, 99.5m, 100m),
            (100m, 100.5m, 99.5m, 100m),
            (100m, 100.5m, 99.5m, 100m),
            (95m, 95.5m, 94.5m, 95m));

        var result = Run(ScriptedStrategy.EntryAt(0, TradeDirection.Long, stop: 99m), bars);

        var trade = Assert.Single(result.Trades);
        Assert.Equal(ExitReason.StopLoss, trade.ExitReason);
        Assert.Equal(94.96m, trade.ExitPrice);   // 95 minus Spread und Stop-Slippage, nicht 99
    }

    [Fact]
    public void Positions_are_closed_before_the_session_ends()
    {
        // 32 Bars zu 15 Minuten füllen 09:00-17:00. Die Zwangsschließung greift 15 Minuten vorher.
        var bars = Fixture.FlatSession(Fixture.Monday, 32);

        var result = Run(ScriptedStrategy.EntryAt(0, TradeDirection.Long, stop: 95m), bars);

        var trade = Assert.Single(result.Trades);
        Assert.Equal(ExitReason.SessionEnd, trade.ExitReason);
        Assert.Equal(Fixture.Monday.AddHours(16).AddMinutes(45), trade.ExitTimeUtc);
        Assert.True(trade.ExitTimeUtc < Fixture.Monday.AddHours(17));
    }

    [Fact]
    public void No_position_survives_into_the_next_trading_day()
    {
        var bars = new List<Candle>();
        bars.AddRange(Fixture.FlatSession(Fixture.Monday, 32));
        bars.AddRange(Fixture.FlatSession(Fixture.Monday.AddDays(1), 32));

        var result = Run(ScriptedStrategy.EntryAt(0, TradeDirection.Long, stop: 95m), bars);

        var trade = Assert.Single(result.Trades);
        Assert.Equal(Fixture.Monday.Date, trade.ExitTimeUtc.Date);
    }

    [Fact]
    public void Commission_is_charged_for_both_sides()
    {
        var symbol = Fixture.Symbol(spread: 0m, commission: 0.01m);
        var bars = Fixture.FlatSession(Fixture.Monday, 32);
        var options = Fixture.Options();
        options.SlippageTicks = 0m;

        var result = Run(
            ScriptedStrategy.EntryAt(0, TradeDirection.Long, stop: 95m), bars, options: options, symbol: symbol);

        var trade = Assert.Single(result.Trades);
        Assert.Equal(0.02m * trade.Quantity, trade.Commission);
        Assert.Equal(trade.GrossPnL - trade.Commission, trade.NetPnL);
    }

    [Fact]
    public void Bars_outside_the_session_are_counted_and_skipped()
    {
        var bars = new List<Candle>
        {
            new Candle(Fixture.Monday.AddHours(7), 100m, 100.5m, 99.5m, 100m, 10m),   // vorbörslich
        };
        bars.AddRange(Fixture.FlatSession(Fixture.Monday, 10));

        var result = Run(new ScriptedStrategy(new Dictionary<int, Func<MarketSnapshot, Signal?>>()), bars);

        Assert.Equal(1, result.BarsOutsideSession);
        Assert.Equal(10, result.BarsProcessed);
    }

    [Fact]
    public void Open_positions_at_the_end_of_the_data_are_closed_and_marked()
    {
        // Nur wenige Bars: Die Session endet nie, also greift die Zwangsschließung nicht.
        var bars = Fixture.FlatSession(Fixture.Monday, 6);

        var result = Run(ScriptedStrategy.EntryAt(0, TradeDirection.Long, stop: 95m), bars);

        var trade = Assert.Single(result.Trades);
        Assert.Equal(ExitReason.EndOfData, trade.ExitReason);
    }

    [Fact]
    public void Rejected_signals_are_counted_by_reason()
    {
        // Stop unter einem Tick Abstand: Die Risikoschicht lehnt ab, der Backtest hält es fest.
        var bars = Fixture.FlatSession(Fixture.Monday, 10);
        var script = new Dictionary<int, Func<MarketSnapshot, Signal?>>();
        for (var i = 0; i < 5; i++)
        {
            script[i] = snapshot => Signal.Entry(
                TradeDirection.Long, snapshot.Current.Close, snapshot.Current.Close - 0.001m, null, null, "zu eng");
        }

        var result = Run(new ScriptedStrategy(script), bars);

        Assert.Empty(result.Trades);
        Assert.Equal(5, result.Rejections[RiskRejectionReason.InvalidStopLoss]);
        Assert.NotEmpty(result.Warnings);
    }

    [Fact]
    public void The_same_input_produces_the_same_output()
    {
        var bars = Fixture.FlatSession(Fixture.Monday, 32);

        var first = Run(ScriptedStrategy.EntryAt(0, TradeDirection.Long, stop: 95m), bars);
        var second = Run(ScriptedStrategy.EntryAt(0, TradeDirection.Long, stop: 95m), bars);

        Assert.Equal(first.Trades.Count, second.Trades.Count);
        Assert.Equal(first.FinalBalance, second.FinalBalance);
        Assert.Equal(
            first.Trades.Select(trade => (trade.EntryPrice, trade.ExitPrice, trade.NetPnL)),
            second.Trades.Select(trade => (trade.EntryPrice, trade.ExitPrice, trade.NetPnL)));
    }

    [Fact]
    public void Position_size_follows_the_risk_budget_and_the_stop_distance()
    {
        // 100.000 Konto, 1 % Risiko = 1.000. Stopabstand rund 5 -> etwa 200 Einheiten.
        var bars = Fixture.FlatSession(Fixture.Monday, 32);

        var result = Run(ScriptedStrategy.EntryAt(0, TradeDirection.Long, stop: 95m), bars);

        var trade = Assert.Single(result.Trades);
        Assert.InRange(trade.Quantity, 195m, 205m);
        Assert.InRange(trade.PlannedRiskAmount, 990m, 1_010m);
    }
}
