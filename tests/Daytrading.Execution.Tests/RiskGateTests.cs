using Daytrading.Execution;
using Daytrading.Execution.Tests.TestSupport;
using Daytrading.Strategies.Model;

namespace Daytrading.Execution.Tests;

/// <summary>
/// Tests der Risikoprüfung. Konto 10.000, also entspricht 1 % genau 100 Kontowährungseinheiten.
/// </summary>
public class RiskGateTests
{
    private static RiskGate Gate(RiskLimits? limits = null, ExecutionSymbol? symbol = null) =>
        new RiskGate(symbol ?? Fixture.Symbol(), limits ?? Fixture.Limits());

    [Fact]
    public void Accepts_a_clean_signal_and_sizes_it_from_the_risk_budget()
    {
        var decision = Gate().Evaluate(
            Fixture.LongEntry(price: 100m, stop: 98m),
            Fixture.Snapshot(Fixture.At(15, 0), 100m),
            Fixture.Account());

        Assert.True(decision.Accepted);
        Assert.Equal(50m, decision.Quantity);      // 100 Risiko / 2.00 Stopabstand
        Assert.Equal(100m, decision.RiskAmount);
        Assert.Equal(1m, decision.RiskPercent);
        Assert.False(decision.WasCapped);
    }

    [Fact]
    public void Never_grants_more_than_the_configured_risk_per_trade()
    {
        // Die Strategie wünscht sich 3 %, erlaubt ist 1 %.
        var decision = Gate().Evaluate(
            Fixture.LongEntry(riskPercent: 3m),
            Fixture.Snapshot(Fixture.At(15, 0), 100m),
            Fixture.Account());

        Assert.True(decision.Accepted);
        Assert.Equal(1m, decision.RiskPercent);
    }

    [Fact]
    public void Honours_a_smaller_risk_wish_from_the_strategy()
    {
        var decision = Gate().Evaluate(
            Fixture.LongEntry(riskPercent: 0.5m),
            Fixture.Snapshot(Fixture.At(15, 0), 100m),
            Fixture.Account());

        Assert.Equal(0.5m, decision.RiskPercent);
        Assert.Equal(25m, decision.Quantity);
    }

    [Fact]
    public void Open_risk_of_running_positions_reduces_the_budget_for_the_next_trade()
    {
        // Offen: 3.5 % Risiko. Frei bleiben 0.5 %, also wird auf 0.5 % gekürzt.
        var open = Fixture.Position("A", entryPrice: 100m, stopLoss: 96.5m, quantity: 100m);

        var decision = Gate().Evaluate(
            Fixture.LongEntry(price: 100m, stop: 98m),
            Fixture.Snapshot(Fixture.At(15, 0), 100m, new[] { open }),
            Fixture.Account(),
            Fixture.OpenRisk(100m, open));

        Assert.True(decision.Accepted);
        Assert.True(decision.WasCapped);
        Assert.Equal(0.5m, decision.RiskPercent);
        Assert.Equal(25m, decision.Quantity);
    }

    [Fact]
    public void Rejects_once_the_open_risk_fills_the_daily_budget()
    {
        // Vier Prozent bereits gebunden: kein Platz mehr, egal wie gut das Signal aussieht.
        var open = Fixture.Position("A", entryPrice: 100m, stopLoss: 96m, quantity: 100m);

        var decision = Gate().Evaluate(
            Fixture.LongEntry(),
            Fixture.Snapshot(Fixture.At(15, 0), 100m, new[] { open }),
            Fixture.Account(),
            Fixture.OpenRisk(100m, open));

        Assert.False(decision.Accepted);
        Assert.Equal(RiskRejectionReason.OpenRiskBudgetExhausted, decision.Reason);
    }

    [Fact]
    public void Four_full_size_positions_fit_the_budget_and_the_fifth_does_not()
    {
        // Der Kern der Regel: bei 1 % je Trade sind vier gleichzeitige Positionen möglich,
        // sieben nicht.
        var gate = Gate();
        var account = Fixture.Account();
        var open = new List<Position>();

        for (var i = 1; i <= 4; i++)
        {
            var decision = gate.Evaluate(
                Fixture.LongEntry(price: 100m, stop: 98m),
                Fixture.Snapshot(Fixture.At(15, 0), 100m, open),
                account,
                Fixture.OpenRisk(100m, open.ToArray()));

            Assert.True(decision.Accepted, $"Position {i} hätte passen müssen: {decision.Message}");
            Assert.Equal(1m, decision.RiskPercent);

            // Gleiche Richtung, aber nicht im Verlust - Nachkaufen in Verlusten wird separat getestet.
            open.Add(Fixture.Position("P" + i, entryPrice: 100m, stopLoss: 98m, quantity: decision.Quantity));
        }

        var fifth = gate.Evaluate(
            Fixture.LongEntry(),
            Fixture.Snapshot(Fixture.At(15, 0), 100m, open),
            account,
            Fixture.OpenRisk(100m, open.ToArray()));

        Assert.False(fifth.Accepted);
        Assert.Equal(RiskRejectionReason.OpenRiskBudgetExhausted, fifth.Reason);
    }

    [Fact]
    public void A_daily_drawdown_eats_into_the_same_budget_as_open_risk()
    {
        // 2 % vom Tageshoch verloren, 1.5 % offen -> nur noch 0.5 % frei.
        var account = Fixture.Account();
        account.BeginTradingDay(Fixture.Day);
        account.ApplyRealizedPnL(-200m);
        var open = Fixture.Position("A", entryPrice: 100m, stopLoss: 98.5m, quantity: 100m);

        var decision = Gate().Evaluate(
            Fixture.LongEntry(),
            Fixture.Snapshot(Fixture.At(15, 0), 100m, new[] { open }),
            account,
            Fixture.OpenRisk(100m, open));

        Assert.True(decision.Accepted);
        Assert.True(decision.WasCapped);
        Assert.Equal(0.5m, decision.RiskPercent);
    }

    [Fact]
    public void Rejects_instead_of_capping_when_capping_is_switched_off()
    {
        var limits = Fixture.Limits();
        limits.CapRiskToRemainingDailyBudget = false;
        var open = Fixture.Position("A", entryPrice: 100m, stopLoss: 96.5m, quantity: 100m);

        var decision = Gate(limits).Evaluate(
            Fixture.LongEntry(),
            Fixture.Snapshot(Fixture.At(15, 0), 100m, new[] { open }),
            Fixture.Account(),
            Fixture.OpenRisk(100m, open));

        Assert.False(decision.Accepted);
        Assert.Equal(RiskRejectionReason.OpenRiskBudgetExhausted, decision.Reason);
    }

    [Fact]
    public void Stops_trading_for_the_day_at_the_realized_daily_loss_limit()
    {
        var account = Fixture.Account();
        account.BeginTradingDay(Fixture.Day);
        account.ApplyRealizedPnL(-400m);

        var decision = Gate().Evaluate(
            Fixture.LongEntry(), Fixture.Snapshot(Fixture.At(15, 0), 100m), account);

        Assert.False(decision.Accepted);
        Assert.Equal(RiskRejectionReason.DailyLossLimit, decision.Reason);
    }

    [Fact]
    public void Stops_trading_at_the_total_drawdown_limit()
    {
        var account = Fixture.Account();
        account.ApplyRealizedPnL(-1_000m);   // 10 % vom Allzeithoch

        var decision = Gate().Evaluate(
            Fixture.LongEntry(), Fixture.Snapshot(Fixture.At(15, 0), 100m), account);

        Assert.False(decision.Accepted);
        Assert.Equal(RiskRejectionReason.TotalDrawdownLimit, decision.Reason);
    }

    [Fact]
    public void Stops_at_the_daily_trade_count()
    {
        var account = Fixture.Account();
        account.BeginTradingDay(Fixture.Day);
        for (var i = 0; i < 6; i++)
        {
            account.RegisterTradeOpened();
        }

        var decision = Gate().Evaluate(
            Fixture.LongEntry(), Fixture.Snapshot(Fixture.At(15, 0), 100m), account);

        Assert.False(decision.Accepted);
        Assert.Equal(RiskRejectionReason.MaxTradesPerDay, decision.Reason);
    }

    [Fact]
    public void Keeps_the_position_count_safety_net()
    {
        // Fünf Positionen ohne Restrisiko (Stop auf dem aktuellen Preis) - das Risikobudget
        // wäre frei, das Sicherheitsnetz greift trotzdem.
        var open = new List<Position>();
        for (var i = 1; i <= 5; i++)
        {
            open.Add(Fixture.Position("P" + i, entryPrice: 100m, stopLoss: 100m, quantity: 10m));
        }

        var decision = Gate().Evaluate(
            Fixture.LongEntry(),
            Fixture.Snapshot(Fixture.At(15, 0), 100m, open),
            Fixture.Account(),
            Fixture.OpenRisk(100m, open.ToArray()));

        Assert.False(decision.Accepted);
        Assert.Equal(RiskRejectionReason.MaxConcurrentPositions, decision.Reason);
    }

    [Fact]
    public void Blocks_averaging_down_into_a_losing_position()
    {
        var losing = Fixture.Position("A", direction: TradeDirection.Long, entryPrice: 100m, stopLoss: 98m, quantity: 10m);

        var decision = Gate().Evaluate(
            Fixture.LongEntry(price: 99m, stop: 97m),
            Fixture.Snapshot(Fixture.At(15, 0), 99m, new[] { losing }),
            Fixture.Account(),
            Fixture.OpenRisk(99m, losing));

        Assert.False(decision.Accepted);
        Assert.Equal(RiskRejectionReason.AveragingDownNotAllowed, decision.Reason);
    }

    [Fact]
    public void Allows_the_opposite_direction_while_a_position_is_losing()
    {
        var losing = Fixture.Position("A", direction: TradeDirection.Long, entryPrice: 100m, stopLoss: 98m, quantity: 10m);

        var decision = Gate().Evaluate(
            Fixture.ShortEntry(price: 99m, stop: 101m),
            Fixture.Snapshot(Fixture.At(15, 0), 99m, new[] { losing }),
            Fixture.Account(),
            Fixture.OpenRisk(99m, losing));

        Assert.True(decision.Accepted);
    }

    [Fact]
    public void Refuses_entries_that_would_be_force_closed_within_minutes()
    {
        // Bar schließt 19:50, Session endet 20:00, Zwangsschließung ab 19:45.
        var decision = Gate().Evaluate(
            Fixture.LongEntry(), Fixture.Snapshot(Fixture.At(19, 35), 100m), Fixture.Account());

        Assert.False(decision.Accepted);
        Assert.Equal(RiskRejectionReason.TooCloseToSessionEnd, decision.Reason);
    }

    [Fact]
    public void Refuses_entries_outside_the_session()
    {
        var decision = Gate().Evaluate(
            Fixture.LongEntry(), Fixture.Snapshot(Fixture.At(20, 0), 100m), Fixture.Account());

        Assert.False(decision.Accepted);
        Assert.Equal(RiskRejectionReason.OutsideTradingWindow, decision.Reason);
    }

    [Fact]
    public void Refuses_a_stop_closer_than_one_tick()
    {
        // Signal.Entry lässt jeden Abstand größer null zu; die Ausführungsschicht verlangt
        // mindestens einen Tick, sonst entstünde eine absurd große Position.
        var decision = Gate().Evaluate(
            Fixture.LongEntry(price: 100m, stop: 99.995m),
            Fixture.Snapshot(Fixture.At(15, 0), 100m),
            Fixture.Account());

        Assert.False(decision.Accepted);
        Assert.Equal(RiskRejectionReason.InvalidStopLoss, decision.Reason);
    }

    [Fact]
    public void Refuses_when_the_minimum_position_size_would_break_the_risk_limit()
    {
        var symbol = Fixture.Symbol(minQuantity: 500m, quantityStep: 500m);

        var decision = Gate(symbol: symbol).Evaluate(
            Fixture.LongEntry(price: 100m, stop: 98m),
            Fixture.Snapshot(Fixture.At(15, 0), 100m),
            Fixture.Account());

        Assert.False(decision.Accepted);
        Assert.Equal(RiskRejectionReason.PositionTooSmallForRiskBudget, decision.Reason);
    }

    [Fact]
    public void Ignores_anything_that_is_not_an_entry_signal()
    {
        var decision = Gate().Evaluate(
            Signal.CloseAll(100m, "raus"), Fixture.Snapshot(Fixture.At(15, 0), 100m), Fixture.Account());

        Assert.False(decision.Accepted);
        Assert.Equal(RiskRejectionReason.NotAnEntrySignal, decision.Reason);
    }

    [Fact]
    public void Every_rejection_carries_a_readable_explanation()
    {
        var account = Fixture.Account();
        account.BeginTradingDay(Fixture.Day);
        account.ApplyRealizedPnL(-400m);

        var decision = Gate().Evaluate(
            Fixture.LongEntry(), Fixture.Snapshot(Fixture.At(15, 0), 100m), account);

        Assert.False(string.IsNullOrWhiteSpace(decision.Message));
        Assert.Contains("4", decision.Message);
    }
}
