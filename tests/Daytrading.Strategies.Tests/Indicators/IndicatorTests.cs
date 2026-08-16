using System;
using System.Linq;
using Daytrading.Strategies.Indicators;
using Daytrading.Strategies.Model;
using Daytrading.Strategies.Tests.TestSupport;

namespace Daytrading.Strategies.Tests.Indicators;

public class RollingWindowTests
{
    [Fact]
    public void Newest_value_is_at_offset_zero()
    {
        var window = new RollingWindow<int>(3);
        window.Add(1);
        window.Add(2);

        Assert.Equal(2, window[0]);
        Assert.Equal(1, window[1]);
        Assert.Equal(2, window.Count);
        Assert.False(window.IsFull);
    }

    [Fact]
    public void Drops_the_oldest_value_when_full()
    {
        var window = new RollingWindow<int>(3);
        foreach (var value in new[] { 1, 2, 3, 4, 5 })
        {
            window.Add(value);
        }

        Assert.True(window.IsFull);
        Assert.Equal(3, window.Count);
        Assert.Equal(new[] { 5, 4, 3 }, window.ToArray());
        Assert.Equal(5L, window.TotalAdded);
        Assert.Throws<ArgumentOutOfRangeException>(() => window[3]);
    }

    [Fact]
    public void Reset_empties_the_window()
    {
        var window = new RollingWindow<int>(2);
        window.Add(1);
        window.Reset();

        Assert.Equal(0, window.Count);
        Assert.Throws<ArgumentOutOfRangeException>(() => window[0]);
    }

    [Fact]
    public void Rejects_non_positive_size()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new RollingWindow<int>(0));
    }
}

public class AverageTrueRangeTests
{
    private static Candle Ohlc(int minuteOffset, decimal high, decimal low, decimal close)
    {
        var open = Math.Min(high, Math.Max(low, close));
        return new Candle(TestMarket.Utc(2024, 3, 1, 14, 0).AddMinutes(minuteOffset), open, high, low, close, 100m);
    }

    [Fact]
    public void Is_not_ready_before_the_period_is_filled()
    {
        var atr = new AverageTrueRange(3);

        atr.Update(Ohlc(0, 10m, 8m, 9m));
        atr.Update(Ohlc(15, 11m, 9m, 10m));

        Assert.False(atr.IsReady);
        Assert.Equal(0m, atr.Value);
    }

    [Fact]
    public void Seeds_with_the_average_true_range_of_the_first_bars()
    {
        var atr = new AverageTrueRange(3);

        // TR: 2 (erste Bar ohne Vorgänger), dann 2 und 2 -> Seed = 2
        atr.Update(Ohlc(0, 10m, 8m, 9m));
        atr.Update(Ohlc(15, 11m, 9m, 10m));
        atr.Update(Ohlc(30, 12m, 10m, 11m));

        Assert.True(atr.IsReady);
        Assert.Equal(2m, atr.Value);
    }

    [Fact]
    public void Applies_wilder_smoothing_after_the_seed()
    {
        var atr = new AverageTrueRange(3);
        atr.Update(Ohlc(0, 10m, 8m, 9m));
        atr.Update(Ohlc(15, 11m, 9m, 10m));
        atr.Update(Ohlc(30, 12m, 10m, 11m));

        // TR = 4, ATR = (2 * 2 + 4) / 3
        atr.Update(Ohlc(45, 15m, 11m, 14m));

        Assert.Equal(8m / 3m, atr.Value, 10);
    }

    [Fact]
    public void True_range_covers_gaps_to_the_previous_close()
    {
        var atr = new AverageTrueRange(1);
        atr.Update(Ohlc(0, 10m, 8m, 9m));

        // Gap nach oben: High - Low wäre nur 1, der Abstand zum Vorgänger-Close ist 11.
        atr.Update(Ohlc(15, 20m, 19m, 19.5m));

        Assert.Equal(11m, atr.LastTrueRange);
    }

    [Fact]
    public void Reset_returns_the_indicator_to_its_initial_state()
    {
        var atr = new AverageTrueRange(2);
        atr.Update(Ohlc(0, 10m, 8m, 9m));
        atr.Update(Ohlc(15, 11m, 9m, 10m));
        Assert.True(atr.IsReady);

        atr.Reset();

        Assert.False(atr.IsReady);
        Assert.Equal(0m, atr.Value);
        Assert.Equal(0m, atr.LastTrueRange);
    }

    [Fact]
    public void Rejects_non_positive_period()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new AverageTrueRange(0));
    }
}
