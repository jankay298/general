using System;
using Daytrading.Strategies.Model;

namespace Daytrading.Strategies.Tests.Model;

public class SignalTests
{
    [Fact]
    public void Long_entry_keeps_prices_and_derives_risk_metrics()
    {
        var signal = Signal.Entry(TradeDirection.Long, referencePrice: 100m, stopLoss: 98m, takeProfit: 106m, riskPercent: 0.5m, reason: "test");

        Assert.Equal(SignalKind.Entry, signal.Kind);
        Assert.Equal(TradeDirection.Long, signal.Direction);
        Assert.Equal(98m, signal.StopLoss);
        Assert.Equal(106m, signal.TakeProfit);
        Assert.Equal(2m, signal.StopDistance);
        Assert.Equal(3m, signal.RewardRiskRatio);
        Assert.Equal(0.5m, signal.RiskPercent);
        Assert.Equal("test", signal.Reason);
    }

    [Theory]
    [InlineData(TradeDirection.Long, 100, 101)]   // Stop über dem Einstieg
    [InlineData(TradeDirection.Long, 100, 100)]   // Stopabstand null
    [InlineData(TradeDirection.Short, 100, 99)]   // Stop unter dem Einstieg
    [InlineData(TradeDirection.Short, 100, 100)]
    public void Rejects_stop_loss_on_the_wrong_side(TradeDirection direction, decimal reference, decimal stop)
    {
        var error = Assert.Throws<ArgumentException>(() => Signal.Entry(direction, reference, stop));

        Assert.Contains("Stop-Loss", error.Message);
    }

    [Theory]
    [InlineData(TradeDirection.Long, 100, 98, 99.9)]
    [InlineData(TradeDirection.Short, 100, 102, 100.1)]
    public void Rejects_take_profit_on_the_wrong_side(TradeDirection direction, decimal reference, decimal stop, decimal target)
    {
        // Ein Ziel entgegen der Handelsrichtung ist immer ein Vorzeichenfehler in der Strategie.
        var error = Assert.Throws<ArgumentException>(() => Signal.Entry(direction, reference, stop, target));

        Assert.Contains("Kursziel", error.Message);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(101)]
    public void Rejects_implausible_risk_percent(decimal riskPercent)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => Signal.Entry(TradeDirection.Long, 100m, 98m, riskPercent: riskPercent));
    }

    [Fact]
    public void Entry_without_take_profit_has_no_reward_risk_ratio()
    {
        var signal = Signal.Entry(TradeDirection.Short, 100m, 102m);

        Assert.Null(signal.TakeProfit);
        Assert.Null(signal.RewardRiskRatio);
        Assert.Null(signal.RiskPercent);
        Assert.Equal(2m, signal.StopDistance);
    }

    [Fact]
    public void Close_all_signal_carries_no_direction_and_no_stop()
    {
        var signal = Signal.CloseAll(100m, "Zeitausstieg");

        Assert.Equal(SignalKind.CloseAll, signal.Kind);
        Assert.Null(signal.Direction);
        Assert.Null(signal.StopLoss);
        Assert.Equal("Zeitausstieg", signal.Reason);
    }
}
