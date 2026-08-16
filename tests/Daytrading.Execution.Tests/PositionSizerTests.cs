using Daytrading.Execution;
using Daytrading.Execution.Tests.TestSupport;
using Daytrading.Strategies.Model;

namespace Daytrading.Execution.Tests;

public class PositionSizerTests
{
    [Fact]
    public void Size_follows_from_risk_amount_and_stop_distance()
    {
        // 100 Risiko, 2.00 Stopabstand, Punktwert 1 -> 50 Einheiten.
        var result = PositionSizer.Calculate(Fixture.Symbol(), riskAmount: 100m, stopDistance: 2m);

        Assert.True(result.IsValid);
        Assert.Equal(50m, result.Quantity);
        Assert.Equal(100m, result.RiskAmount);
    }

    [Fact]
    public void A_wider_stop_gives_a_smaller_position_at_the_same_risk()
    {
        var narrow = PositionSizer.Calculate(Fixture.Symbol(), 100m, 1m);
        var wide = PositionSizer.Calculate(Fixture.Symbol(), 100m, 4m);

        Assert.Equal(100m, narrow.Quantity);
        Assert.Equal(25m, wide.Quantity);
        Assert.Equal(narrow.RiskAmount, wide.RiskAmount);
    }

    [Fact]
    public void Rounds_down_to_the_quantity_step_so_the_risk_is_never_exceeded()
    {
        // 100 / 3 = 33.33 Einheiten -> 33, nicht 34.
        var result = PositionSizer.Calculate(Fixture.Symbol(quantityStep: 1m), 100m, 3m);

        Assert.Equal(33m, result.Quantity);
        Assert.Equal(99m, result.RiskAmount);
        Assert.True(result.RiskAmount <= 100m);
    }

    [Fact]
    public void Honours_a_coarse_quantity_step()
    {
        var result = PositionSizer.Calculate(Fixture.Symbol(minQuantity: 0.01m, quantityStep: 0.01m), 100m, 3m);

        Assert.Equal(33.33m, result.Quantity);
    }

    [Fact]
    public void Applies_the_point_value_of_the_instrument()
    {
        // Index-CFD: eine Preisbewegung von 1.0 ist 5 Kontowährungseinheiten je Einheit wert.
        var symbol = Fixture.Symbol(valuePerPricePointPerUnit: 5m);

        var result = PositionSizer.Calculate(symbol, riskAmount: 100m, stopDistance: 4m);

        Assert.Equal(5m, result.Quantity);      // 100 / (4 * 5)
        Assert.Equal(100m, result.RiskAmount);
    }

    [Fact]
    public void Rejects_when_the_minimum_size_would_exceed_the_allowed_risk()
    {
        // Mindestgröße 100 Einheiten, erlaubtes Risiko reicht nur für 50.
        var symbol = Fixture.Symbol(minQuantity: 100m, quantityStep: 100m);

        var result = PositionSizer.Calculate(symbol, riskAmount: 100m, stopDistance: 2m);

        Assert.False(result.IsValid);
        Assert.Contains("Mindestgröße", result.Message);
        Assert.Equal(0m, result.Quantity);
    }

    [Fact]
    public void Caps_at_the_maximum_size()
    {
        var symbol = Fixture.Symbol(maxQuantity: 20m);

        var result = PositionSizer.Calculate(symbol, riskAmount: 100m, stopDistance: 2m);

        Assert.True(result.IsValid);
        Assert.Equal(20m, result.Quantity);
        Assert.Equal(40m, result.RiskAmount);   // weniger Risiko als erlaubt, nie mehr
    }

    [Theory]
    [InlineData(0, 2)]
    [InlineData(-100, 2)]
    [InlineData(100, 0)]
    [InlineData(100, -2)]
    public void Rejects_impossible_inputs(decimal riskAmount, decimal stopDistance)
    {
        var result = PositionSizer.Calculate(Fixture.Symbol(), riskAmount, stopDistance);

        Assert.False(result.IsValid);
    }
}

public class OpenRiskTests
{
    [Fact]
    public void Risk_is_measured_from_the_current_price_to_the_stop()
    {
        // Einstieg 100, Stop 98, aktuell 99: es stehen noch 1.00 je Einheit auf dem Spiel,
        // nicht 2.00. Die erste Hälfte steckt bereits im Tages-Drawdown.
        var position = Fixture.Position(entryPrice: 100m, stopLoss: 98m, quantity: 10m);

        var item = new OpenRiskItem(position, currentPrice: 99m, valuePerPricePointPerUnit: 1m);

        Assert.Equal(10m, item.RiskAmount);
    }

    [Fact]
    public void Risk_is_zero_once_the_stop_sits_in_profit()
    {
        var position = Fixture.Position(entryPrice: 100m, stopLoss: 101m, quantity: 10m);

        var item = new OpenRiskItem(position, currentPrice: 103m, valuePerPricePointPerUnit: 1m);

        Assert.Equal(20m, item.RiskAmount);

        var beyondStop = new OpenRiskItem(position, currentPrice: 100.5m, valuePerPricePointPerUnit: 1m);
        Assert.Equal(0m, beyondStop.RiskAmount);
    }

    [Fact]
    public void Short_positions_risk_the_distance_up_to_the_stop()
    {
        var position = Fixture.Position(
            direction: TradeDirection.Short,
            entryPrice: 100m,
            stopLoss: 102m,
            quantity: 10m);

        var item = new OpenRiskItem(position, currentPrice: 101m, valuePerPricePointPerUnit: 1m);

        Assert.Equal(10m, item.RiskAmount);
    }

    [Fact]
    public void Totals_add_up_across_positions_and_convert_to_percent()
    {
        var items = Fixture.OpenRisk(
            100m,
            Fixture.Position("A", entryPrice: 100m, stopLoss: 98m, quantity: 10m),   // 20
            Fixture.Position("B", entryPrice: 100m, stopLoss: 97m, quantity: 10m));  // 30

        Assert.Equal(50m, OpenRisk.TotalAmount(items));
        Assert.Equal(0.5m, OpenRisk.TotalPercent(items, 10_000m));
    }

    [Fact]
    public void An_empty_book_carries_no_risk()
    {
        Assert.Equal(0m, OpenRisk.TotalAmount(new List<OpenRiskItem>()));
        Assert.Equal(0m, OpenRisk.TotalPercent(new List<OpenRiskItem>(), 10_000m));
    }
}
