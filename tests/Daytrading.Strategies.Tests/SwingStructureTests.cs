using System;
using System.Linq;
using Daytrading.Strategies.Indicators;
using Daytrading.Strategies.Model;
using Daytrading.Strategies.Tests.TestSupport;

namespace Daytrading.Strategies.Tests;

/// <summary>
/// Erkennung der Wendepunkte. Der Unterschied zum tiefsten Tief der letzten n Bars ist der
/// ganze Zweck: Ein Wendepunkt ist eine Stelle, an der der Markt gedreht hat, kein beliebiger
/// Ausschlag mitten in einer Bewegung.
/// </summary>
public class SwingStructureTests
{
    private static Candle Bar(int minute, decimal low, decimal high) =>
        new Candle(
            TestMarket.Utc(2024, 3, 4, 10).AddMinutes(minute * 5),
            (low + high) / 2m, high, low, (low + high) / 2m, 1_000m);

    /// <summary>Kursverlauf aus (Tief, Hoch)-Paaren, eine Bar je Paar.</summary>
    private static SwingStructure Feed(SwingStructure structure, params (decimal Low, decimal High)[] bars)
    {
        for (var i = 0; i < bars.Length; i++)
        {
            structure.Update(Bar(i, bars[i].Low, bars[i].High));
        }

        return structure;
    }

    [Fact]
    public void Finds_a_low_that_has_higher_lows_on_both_sides()
    {
        var structure = Feed(
            new SwingStructure(leftBars: 2, rightBars: 2),
            (10, 12), (9, 11), (8, 10), (9, 11), (10, 12));

        var low = Assert.Single(structure.Lows);
        Assert.Equal(8m, low.Price);
    }

    [Fact]
    public void Does_not_confirm_a_low_before_enough_bars_have_followed()
    {
        // Nach zwei Bars ist noch nicht entscheidbar, ob der Markt dort gedreht hat. Frueher
        // zu bestaetigen hiesse, in die Zukunft zu sehen.
        var structure = Feed(new SwingStructure(leftBars: 2, rightBars: 2), (10, 12), (9, 11), (8, 10));

        Assert.Empty(structure.Lows);
    }

    [Fact]
    public void Ignores_a_new_extreme_that_is_only_the_end_of_a_run()
    {
        // Durchgehend fallend: Es gibt kein Tief mit hoeheren Tiefs auf beiden Seiten, also
        // auch keinen Wendepunkt - obwohl es reichlich "tiefste Tiefs der letzten n Bars" gibt.
        var structure = Feed(
            new SwingStructure(leftBars: 2, rightBars: 2),
            (10, 12), (9, 11), (8, 10), (7, 9), (6, 8), (5, 7));

        Assert.Empty(structure.Lows);
    }

    [Fact]
    public void Counts_a_level_that_is_reached_twice_as_one_level_touched_twice()
    {
        // Doppeltief: im Chart auffaelliger als ein einzelnes, also die bessere Vermutung
        // darueber, wo Auftraege liegen.
        var structure = new SwingStructure(leftBars: 1, rightBars: 1, mergeTolerance: 0.5m);
        Feed(structure,
            (10, 12), (8, 11), (10, 12),
            (10, 12), (8.2m, 11), (10, 12));

        var low = Assert.Single(structure.Lows);
        Assert.Equal(2, low.Touches);
    }

    [Fact]
    public void Keeps_two_levels_apart_when_they_are_further_than_the_tolerance()
    {
        var structure = new SwingStructure(leftBars: 1, rightBars: 1, mergeTolerance: 0.1m);
        Feed(structure,
            (10, 12), (8, 11), (10, 12),
            (10, 12), (9, 11), (10, 12));

        Assert.Equal(2, structure.Lows.Count);
        Assert.Equal(new[] { 9m, 8m }, structure.Lows.Select(level => level.Price));
    }

    [Fact]
    public void Reports_the_newest_level_first()
    {
        var structure = new SwingStructure(leftBars: 1, rightBars: 1);
        Feed(structure,
            (10, 12), (8, 11), (10, 12),
            (10, 12), (7, 11), (10, 12));

        Assert.Equal(7m, structure.Lows[0].Price);
    }

    [Fact]
    public void Tracks_how_old_a_level_is_in_bars()
    {
        var structure = new SwingStructure(leftBars: 1, rightBars: 1);
        Feed(structure, (10, 12), (8, 11), (10, 12), (10, 12), (10, 12));

        // Das Tief lag auf Bar 2 von 5 - der Abstand ist das Alter.
        Assert.Equal(3, structure.BarsSeen - structure.Lows[0].BarIndex);
    }

    [Fact]
    public void Rejects_a_configuration_without_bars_on_both_sides()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new SwingStructure(leftBars: 0, rightBars: 2));
    }
}
