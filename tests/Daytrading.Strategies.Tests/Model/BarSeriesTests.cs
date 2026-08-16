using System;
using Daytrading.Strategies.Model;
using Daytrading.Strategies.Tests.TestSupport;

namespace Daytrading.Strategies.Tests.Model;

public class BarSeriesTests
{
    private static Candle BarAt(int hour, int minute, decimal close = 100m) =>
        TestMarket.Bar(TestMarket.Utc(2024, 3, 1, hour, minute), close);

    [Fact]
    public void Last_counts_backwards_from_the_newest_bar()
    {
        var series = new BarSeries();
        series.Append(BarAt(14, 0, 100m));
        series.Append(BarAt(14, 15, 101m));
        series.Append(BarAt(14, 30, 102m));

        Assert.Equal(3, series.Count);
        Assert.Equal(102m, series.Last().Close);
        Assert.Equal(101m, series.Last(1).Close);
        Assert.Equal(100m, series.Last(2).Close);
    }

    [Fact]
    public void Indexer_is_chronological()
    {
        var series = new BarSeries();
        series.Append(BarAt(14, 0, 100m));
        series.Append(BarAt(14, 15, 101m));

        Assert.Equal(100m, series[0].Close);
        Assert.Equal(101m, series[1].Close);
        Assert.Throws<ArgumentOutOfRangeException>(() => series[2]);
    }

    [Fact]
    public void TryLast_reports_insufficient_history_instead_of_throwing()
    {
        var series = new BarSeries();
        series.Append(BarAt(14, 0));

        Assert.True(series.TryLast(0, out var current));
        Assert.Equal(100m, current.Close);
        Assert.False(series.TryLast(1, out _));
        Assert.False(series.TryLast(-1, out _));
        Assert.Throws<ArgumentOutOfRangeException>(() => series.Last(1));
    }

    [Fact]
    public void Rejects_duplicate_timestamps()
    {
        var series = new BarSeries();
        series.Append(BarAt(14, 0));

        var error = Assert.Throws<InvalidOperationException>(() => series.Append(BarAt(14, 0)));

        Assert.Contains("Doppelter", error.Message);
    }

    [Fact]
    public void Rejects_out_of_order_bars()
    {
        var series = new BarSeries();
        series.Append(BarAt(14, 15));

        var error = Assert.Throws<InvalidOperationException>(() => series.Append(BarAt(14, 0)));

        Assert.Contains("chronologisch", error.Message);
    }
}
