using Daytrading.Data;
using Daytrading.Data.Normalization;
using Daytrading.Strategies.Model;

namespace Daytrading.Data.Tests;

public class BarNormalizerTests
{
    private static DateTime T(int hour, int minute) => new DateTime(2024, 3, 1, hour, minute, 0, DateTimeKind.Utc);

    private static Candle Bar(DateTime time, decimal close = 100m) =>
        new Candle(time, close, close + 1m, close - 1m, close, 10m);

    [Fact]
    public void Sorts_bars_and_reports_the_disorder()
    {
        var bars = new[] { Bar(T(15, 0)), Bar(T(14, 0)), Bar(T(16, 0)) };

        var result = BarNormalizer.Normalize(bars, out var report);

        Assert.Equal(new[] { T(14, 0), T(15, 0), T(16, 0) }, result.Select(bar => bar.OpenTimeUtc));
        Assert.Equal(1, report.OutOfOrderBars);
    }

    [Fact]
    public void Drops_identical_duplicates_without_drama()
    {
        var bars = new[] { Bar(T(14, 0)), Bar(T(14, 0)), Bar(T(15, 0)) };

        var result = BarNormalizer.Normalize(bars, out var report);

        Assert.Equal(2, result.Count);
        Assert.Equal(1, report.IdenticalDuplicates);
        Assert.Equal(0, report.ConflictingDuplicates);
    }

    [Fact]
    public void Flags_duplicates_that_disagree_about_the_price()
    {
        // Zwei verschiedene Kurse zum selben Zeitpunkt heißt: vermischte Quellen oder kaputte Datei.
        var bars = new[] { Bar(T(14, 0), 100m), Bar(T(14, 0), 105m) };

        var result = BarNormalizer.Normalize(bars, out var report);

        Assert.Single(result);
        Assert.Equal(1, report.ConflictingDuplicates);
        Assert.Single(report.Conflicts);
        Assert.Equal(100m, result[0].Close);   // der erste Wert gewinnt, deterministisch
    }

    [Fact]
    public void Refuses_bars_that_are_not_utc()
    {
        // Candle selbst verlangt UTC - der Test belegt, dass der Fehler bis hierher trägt.
        Assert.Throws<ArgumentException>(
            () => new Candle(new DateTime(2024, 3, 1, 14, 0, 0, DateTimeKind.Local), 100m, 101m, 99m, 100m, 1m));
    }

    [Fact]
    public void Never_invents_bars_to_fill_gaps()
    {
        var bars = new[] { Bar(T(14, 0)), Bar(T(16, 0)) };

        var result = BarNormalizer.Normalize(bars, out _);

        Assert.Equal(2, result.Count);
    }
}

public class BarAggregatorTests
{
    private static DateTime T(int hour, int minute) => new DateTime(2024, 3, 1, hour, minute, 0, DateTimeKind.Utc);

    private static Candle Minute(int minute, decimal open, decimal high, decimal low, decimal close, decimal volume = 1m) =>
        new Candle(T(14, minute), open, high, low, close, volume);

    [Fact]
    public void Builds_a_five_minute_bar_from_five_one_minute_bars()
    {
        var source = new[]
        {
            Minute(0, 100m, 101m, 99m, 100.5m, 10m),
            Minute(1, 100.5m, 103m, 100m, 102m, 20m),
            Minute(2, 102m, 102.5m, 98m, 99m, 30m),
            Minute(3, 99m, 100m, 98.5m, 99.5m, 40m),
            Minute(4, 99.5m, 101m, 99m, 100m, 50m),
        };

        var result = BarAggregator.Aggregate(source, Timeframe.M1, Timeframe.M5);

        var bar = Assert.Single(result);
        Assert.Equal(T(14, 0), bar.OpenTimeUtc);
        Assert.Equal(100m, bar.Open);      // Open der ersten Bar
        Assert.Equal(103m, bar.High);      // höchstes High
        Assert.Equal(98m, bar.Low);        // tiefstes Low
        Assert.Equal(100m, bar.Close);     // Close der letzten Bar
        Assert.Equal(150m, bar.Volume);    // Summe
    }

    [Fact]
    public void Aligns_buckets_to_a_fixed_grid_regardless_of_where_the_series_starts()
    {
        // Beginn 14:03 - die erste 5-Minuten-Bar ist trotzdem die von 14:00.
        var source = new[]
        {
            Minute(3, 100m, 101m, 99m, 100m),
            Minute(4, 100m, 102m, 100m, 101m),
            Minute(5, 101m, 103m, 101m, 102m),
        };

        var result = BarAggregator.Aggregate(source, Timeframe.M1, Timeframe.M5);

        Assert.Equal(2, result.Count);
        Assert.Equal(T(14, 0), result[0].OpenTimeUtc);
        Assert.Equal(T(14, 5), result[1].OpenTimeUtc);
    }

    [Fact]
    public void Leaves_empty_intervals_empty_instead_of_carrying_the_last_price_forward()
    {
        var source = new[] { Minute(0, 100m, 101m, 99m, 100m), Minute(11, 100m, 101m, 99m, 100m) };

        var result = BarAggregator.Aggregate(source, Timeframe.M1, Timeframe.M5);

        Assert.Equal(2, result.Count);
        Assert.Equal(T(14, 0), result[0].OpenTimeUtc);
        Assert.Equal(T(14, 10), result[1].OpenTimeUtc);   // 14:05 fehlt und bleibt weg
    }

    [Fact]
    public void Refuses_to_produce_a_finer_resolution_than_the_source()
    {
        var source = new[] { Minute(0, 100m, 101m, 99m, 100m) };

        Assert.Throws<ArgumentException>(() => BarAggregator.Aggregate(source, Timeframe.M5, Timeframe.M1));
    }

    [Fact]
    public void Refuses_targets_that_are_not_a_whole_multiple()
    {
        var source = new[] { Minute(0, 100m, 101m, 99m, 100m) };

        Assert.Throws<ArgumentException>(
            () => BarAggregator.Aggregate(source, Timeframe.FromMinutes(2), Timeframe.FromMinutes(5)));
    }

    [Fact]
    public void Returns_the_source_untouched_for_an_identical_timeframe()
    {
        var source = new[] { Minute(0, 100m, 101m, 99m, 100m) };

        Assert.Same(source, BarAggregator.Aggregate(source, Timeframe.M1, Timeframe.M1));
    }
}
