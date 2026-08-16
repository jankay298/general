using Daytrading.Execution;
using Daytrading.Execution.Tests.TestSupport;
using Daytrading.Strategies.Model;

namespace Daytrading.Execution.Tests;

public class SessionGuardTests
{
    private static List<ExecutionInstruction> Run(
        DateTime barOpenUtc,
        IReadOnlyList<Position> positions,
        RiskLimits? limits = null,
        DateTime? sessionDay = null)
    {
        var instructions = new List<ExecutionInstruction>();
        new SessionGuard(limits ?? Fixture.Limits())
            .AppendExits(Fixture.Snapshot(barOpenUtc, 100m, positions, sessionDay), positions, instructions);
        return instructions;
    }

    [Fact]
    public void Leaves_positions_alone_in_the_middle_of_the_session()
    {
        var instructions = Run(Fixture.At(15, 0), new[] { Fixture.Position() });

        Assert.Empty(instructions);
    }

    [Fact]
    public void Closes_everything_inside_the_force_flat_window()
    {
        // Bar schließt 19:50, Session endet 20:00, Vorlauf 15 Minuten.
        var instructions = Run(Fixture.At(19, 35), new[] { Fixture.Position("A"), Fixture.Position("B") });

        Assert.Equal(2, instructions.Count);
        Assert.All(instructions, instruction =>
        {
            Assert.Equal(ExecutionInstructionKind.ClosePosition, instruction.Kind);
            Assert.Equal(ExitReason.SessionEnd, instruction.ExitReason);
        });
        Assert.Contains("Zwangsschließung", instructions[0].Reason);
    }

    [Fact]
    public void Closes_everything_after_the_session_has_ended()
    {
        var instructions = Run(Fixture.At(20, 30), new[] { Fixture.Position() });

        Assert.Single(instructions);
        Assert.Equal(ExitReason.SessionEnd, instructions[0].ExitReason);
    }

    [Fact]
    public void The_force_flat_window_is_configurable()
    {
        var limits = Fixture.Limits();
        limits.ForceFlatMinutesBeforeSessionEnd = 60;

        // 19:00 Barschluss: 60 Minuten Rest, mit dem größeren Vorlauf bereits im Fenster.
        var instructions = Run(Fixture.At(18, 45), new[] { Fixture.Position() }, limits);

        Assert.Single(instructions);
    }

    [Fact]
    public void Closes_a_position_that_reached_the_maximum_holding_time()
    {
        var limits = Fixture.Limits();
        limits.MaxHoldingMinutes = 60;
        var position = Fixture.Position(entryTimeUtc: Fixture.At(14, 0));

        // Barschluss 15:00 - genau 60 Minuten gehalten.
        var instructions = Run(Fixture.At(14, 45), new[] { position }, limits);

        Assert.Single(instructions);
        Assert.Equal(ExitReason.MaxHoldingTime, instructions[0].ExitReason);
        Assert.Contains("Haltedauer", instructions[0].Reason);
    }

    [Fact]
    public void Keeps_a_position_that_is_still_within_the_maximum_holding_time()
    {
        var limits = Fixture.Limits();
        limits.MaxHoldingMinutes = 60;
        var position = Fixture.Position(entryTimeUtc: Fixture.At(14, 0));

        var instructions = Run(Fixture.At(14, 30), new[] { position }, limits);

        Assert.Empty(instructions);
    }

    [Fact]
    public void Closes_a_position_that_survived_into_the_next_trading_day()
    {
        // Sollte nach der Zwangsschließung nie vorkommen - genau deshalb wird es geprüft.
        var yesterday = Fixture.Position(entryTimeUtc: Fixture.At(15, 0));
        var nextDay = Fixture.Day.AddDays(1);

        var instructions = Run(nextDay.AddHours(15), new[] { yesterday }, sessionDay: nextDay);

        Assert.Single(instructions);
        Assert.Equal(ExitReason.OvernightGuard, instructions[0].ExitReason);
    }

    [Fact]
    public void Does_nothing_without_open_positions()
    {
        Assert.Empty(Run(Fixture.At(19, 45), Array.Empty<Position>()));
    }
}
