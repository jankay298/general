using Daytrading.Execution;
using Daytrading.Execution.Tests.TestSupport;
using Daytrading.Strategies;
using Daytrading.Strategies.Model;

namespace Daytrading.Execution.Tests;

public class ExecutionEngineTests
{
    private static ExecutionEngine Engine(RiskLimits? limits = null, IStrategyLog? log = null) =>
        new ExecutionEngine(Fixture.Symbol(), limits ?? Fixture.Limits(), log);

    [Fact]
    public void Turns_an_accepted_signal_into_an_open_instruction()
    {
        var account = Fixture.Account();

        var instructions = Engine().OnBar(
            Fixture.Snapshot(Fixture.At(15, 0), 100m),
            account,
            Fixture.LongEntry(price: 100m, stop: 98m, takeProfit: 106m));

        var instruction = Assert.Single(instructions);
        Assert.Equal(ExecutionInstructionKind.OpenPosition, instruction.Kind);
        Assert.Equal(TradeDirection.Long, instruction.Direction);
        Assert.Equal(50m, instruction.Quantity);
        Assert.Equal(98m, instruction.StopLoss);
        Assert.Equal(106m, instruction.TakeProfit);
        Assert.Equal(100m, instruction.RiskAmount);
        Assert.Equal(Fixture.At(15, 15), instruction.DecisionTimeUtc);
        Assert.Equal(1, account.TradesToday);
    }

    [Fact]
    public void Emits_nothing_without_a_signal()
    {
        var instructions = Engine().OnBar(Fixture.Snapshot(Fixture.At(15, 0), 100m), Fixture.Account(), null);

        Assert.Empty(instructions);
    }

    [Fact]
    public void Starts_the_trading_day_on_the_account()
    {
        var account = Fixture.Account();

        Engine().OnBar(Fixture.Snapshot(Fixture.At(15, 0), 100m), account, null);

        Assert.Equal(Fixture.Day, account.TradingDay);
    }

    [Fact]
    public void Resets_the_daily_trade_count_when_the_day_changes()
    {
        var engine = Engine();
        var account = Fixture.Account();

        for (var i = 0; i < 6; i++)
        {
            engine.OnBar(Fixture.Snapshot(Fixture.At(15, 0), 100m), account, Fixture.LongEntry());
        }

        var blocked = engine.OnBar(Fixture.Snapshot(Fixture.At(15, 0), 100m), account, Fixture.LongEntry());
        Assert.Empty(blocked);
        Assert.Equal(RiskRejectionReason.MaxTradesPerDay, engine.LastDecision!.Reason);

        var nextDay = Fixture.Day.AddDays(1);
        var allowed = engine.OnBar(
            Fixture.Snapshot(nextDay.AddHours(15), 100m, sessionDay: nextDay), account, Fixture.LongEntry());

        Assert.Single(allowed);
        Assert.Equal(1, account.TradesToday);
    }

    [Fact]
    public void Force_closes_open_positions_and_refuses_new_ones_before_the_session_ends()
    {
        var engine = Engine();
        var open = new[] { Fixture.Position("A") };

        var instructions = engine.OnBar(
            Fixture.Snapshot(Fixture.At(19, 35), 100m, open),
            Fixture.Account(),
            Fixture.LongEntry());

        var instruction = Assert.Single(instructions);
        Assert.Equal(ExecutionInstructionKind.ClosePosition, instruction.Kind);
        Assert.Equal(ExitReason.SessionEnd, instruction.ExitReason);
        Assert.Equal(RiskRejectionReason.TooCloseToSessionEnd, engine.LastDecision!.Reason);
    }

    [Fact]
    public void Closes_every_position_on_a_strategy_exit_signal()
    {
        var open = new[] { Fixture.Position("A"), Fixture.Position("B") };

        var instructions = Engine().OnBar(
            Fixture.Snapshot(Fixture.At(15, 0), 100m, open),
            Fixture.Account(),
            Signal.CloseAll(100m, "Mittelwert erreicht"));

        Assert.Equal(2, instructions.Count);
        Assert.All(instructions, i => Assert.Equal(ExitReason.StrategyExit, i.ExitReason));
        Assert.Contains("Mittelwert", instructions[0].Reason);
    }

    [Fact]
    public void Never_closes_the_same_position_twice()
    {
        // Zwangsschließung und Ausstiegssignal treffen auf derselben Bar zusammen.
        var open = new[] { Fixture.Position("A") };

        var instructions = Engine().OnBar(
            Fixture.Snapshot(Fixture.At(19, 35), 100m, open),
            Fixture.Account(),
            Signal.CloseAll(100m, "raus"));

        var instruction = Assert.Single(instructions);
        Assert.Equal(ExitReason.SessionEnd, instruction.ExitReason);
    }

    [Fact]
    public void Counts_existing_positions_against_the_risk_budget()
    {
        // Ohne explizite Kontoliste bewertet die Schicht die Positionen des Snapshots
        // mit dem Bar-Close - 3.5 % offen lassen nur noch 0.5 % zu.
        var open = new[] { Fixture.Position("A", entryPrice: 100m, stopLoss: 96.5m, quantity: 100m) };

        var instructions = Engine().OnBar(
            Fixture.Snapshot(Fixture.At(15, 0), 100m, open),
            Fixture.Account(),
            Fixture.LongEntry(price: 100m, stop: 98m));

        var instruction = Assert.Single(instructions);
        Assert.Equal(ExecutionInstructionKind.OpenPosition, instruction.Kind);
        Assert.Equal(25m, instruction.Quantity);
        Assert.Equal(0.5m, instruction.RiskPercent);
    }

    [Fact]
    public void Logs_the_reason_for_every_rejection()
    {
        var log = new InMemoryStrategyLog();
        var account = Fixture.Account();
        account.ApplyRealizedPnL(-1_000m);

        Engine(log: log).OnBar(Fixture.Snapshot(Fixture.At(15, 0), 100m), account, Fixture.LongEntry());

        Assert.Contains(log.Entries, entry => entry.Contains("TotalDrawdownLimit"));
    }

    [Fact]
    public void Logs_a_strategy_error_when_the_stop_is_unusable()
    {
        var log = new InMemoryStrategyLog();

        Engine(log: log).OnBar(
            Fixture.Snapshot(Fixture.At(15, 0), 100m),
            Fixture.Account(),
            Fixture.LongEntry(price: 100m, stop: 99.999m));

        Assert.Contains(log.Entries, entry => entry.StartsWith("[Error]") && entry.Contains("InvalidStopLoss"));
    }

    [Fact]
    public void Rejects_a_contradictory_risk_configuration_at_construction_time()
    {
        var limits = Fixture.Limits();
        limits.RiskPerTradePercent = 5m;   // größer als MaxOpenRiskPercent

        var error = Assert.Throws<RiskConfigurationException>(() => Engine(limits));

        Assert.Contains("RiskPerTradePercent", error.Message);
    }
}
