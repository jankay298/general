using Daytrading.Execution.Tests.TestSupport;
using Daytrading.Strategies.Model;

namespace Daytrading.Execution.Tests;

/// <summary>
/// Nachziehen des Stops. Long-Beispiel durchgehend: Einstieg 100, Stop 98, also 2.00 Risiko.
/// </summary>
public class StopManagementTests
{
    private static Position Long(decimal stop = 98m) =>
        new Position("T1", "TEST", TradeDirection.Long, Fixture.Day.AddHours(10), 100m, stop, 110m, 10m, "S");

    private static Position Short(decimal stop = 102m) =>
        new Position("T1", "TEST", TradeDirection.Short, Fixture.Day.AddHours(10), 100m, stop, 90m, 10m, "S");

    private static RiskLimits Limits(decimal breakeven = 0m, decimal trail = 0m, decimal trailStart = 1m)
    {
        var limits = RiskLimits.Default;
        limits.BreakevenAfterR = breakeven;
        limits.TrailStopDistanceR = trail;
        limits.TrailStartsAfterR = trailStart;
        return limits;
    }

    [Fact]
    public void Does_nothing_while_both_rules_are_switched_off()
    {
        Assert.Null(StopManagement.Adjust(Long(), 2m, 110m, Limits()));
    }

    [Fact]
    public void Moves_the_stop_to_the_entry_once_the_trade_is_far_enough_ahead()
    {
        // Bestkurs 102 = +1R. Bei Schwelle 1R rueckt der Stop auf 100.
        Assert.Equal(100m, StopManagement.Adjust(Long(), 2m, 102m, Limits(breakeven: 1m)));
    }

    [Fact]
    public void Waits_for_the_threshold_before_moving_to_the_entry()
    {
        // Bestkurs 101 = +0.5R, zu wenig.
        Assert.Null(StopManagement.Adjust(Long(), 2m, 101m, Limits(breakeven: 1m)));
    }

    [Fact]
    public void Trails_at_the_configured_distance_behind_the_best_price()
    {
        // Bestkurs 106 = +3R, Abstand 1R = 2.00 darunter.
        Assert.Equal(104m, StopManagement.Adjust(Long(), 2m, 106m, Limits(trail: 1m)));
    }

    [Fact]
    public void Never_moves_the_stop_away_from_the_price()
    {
        // Der Stop steht schon auf 105, der Trailing-Vorschlag waere 104 - er wird verworfen.
        // Ein Stop, der zuruecklaufen darf, ist kein Stop mehr.
        Assert.Null(StopManagement.Adjust(Long(stop: 105m), 2m, 106m, Limits(trail: 1m)));
    }

    [Fact]
    public void Works_the_same_way_for_a_short_position()
    {
        Assert.Equal(100m, StopManagement.Adjust(Short(), 2m, 98m, Limits(breakeven: 1m)));
        Assert.Equal(96m, StopManagement.Adjust(Short(), 2m, 94m, Limits(trail: 1m)));
    }

    [Fact]
    public void Takes_the_tighter_of_the_two_rules_when_both_are_active()
    {
        // Break-even schlaegt 100 vor, Trailing 104. Der naeher am Kurs liegende gewinnt.
        Assert.Equal(104m, StopManagement.Adjust(Long(), 2m, 106m, Limits(breakeven: 1m, trail: 1m)));
    }

    [Fact]
    public void Ignores_a_position_whose_initial_risk_is_not_positive()
    {
        Assert.Null(StopManagement.Adjust(Long(), 0m, 110m, Limits(breakeven: 1m)));
    }
}
