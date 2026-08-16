using System;
using Daytrading.Strategies.Model;
using Daytrading.Strategies.Tests.TestSupport;

namespace Daytrading.Strategies.Tests.Model;

public class CandleTests
{
    [Fact]
    public void Rejects_non_utc_timestamps()
    {
        var local = new DateTime(2024, 3, 1, 14, 0, 0, DateTimeKind.Local);

        var error = Assert.Throws<ArgumentException>(() => new Candle(local, 10m, 11m, 9m, 10.5m, 100m));

        Assert.Contains("UTC", error.Message);
    }

    [Fact]
    public void Rejects_high_below_low()
    {
        var time = TestMarket.Utc(2024, 3, 1, 14, 0);

        var error = Assert.Throws<ArgumentException>(() => new Candle(time, 10m, 9m, 11m, 10m, 100m));

        Assert.Contains("High", error.Message);
    }

    [Theory]
    [InlineData(12, 11, 9, 10)]  // Open über High
    [InlineData(10, 11, 9, 12)]  // Close über High
    [InlineData(8, 11, 9, 10)]   // Open unter Low
    [InlineData(10, 11, 9, 8)]   // Close unter Low
    public void Rejects_inconsistent_ohlc(decimal open, decimal high, decimal low, decimal close)
    {
        var time = TestMarket.Utc(2024, 3, 1, 14, 0);

        Assert.Throws<ArgumentException>(() => new Candle(time, open, high, low, close, 100m));
    }

    [Fact]
    public void Rejects_negative_volume()
    {
        var time = TestMarket.Utc(2024, 3, 1, 14, 0);

        Assert.Throws<ArgumentException>(() => new Candle(time, 10m, 11m, 9m, 10m, -1m));
    }

    [Fact]
    public void Exposes_derived_values()
    {
        var candle = new Candle(TestMarket.Utc(2024, 3, 1, 14, 0), 10m, 12m, 9m, 11m, 500m);

        Assert.Equal(3m, candle.Range);
        Assert.Equal(1m, candle.Body);
        Assert.True(candle.IsBullish);
        Assert.False(candle.IsBearish);
        Assert.Equal((12m + 9m + 11m) / 3m, candle.TypicalPrice);
    }

    [Fact]
    public void Equality_is_by_value()
    {
        var time = TestMarket.Utc(2024, 3, 1, 14, 0);
        var a = new Candle(time, 10m, 12m, 9m, 11m, 500m);
        var b = new Candle(time, 10m, 12m, 9m, 11m, 500m);
        var c = new Candle(time, 10m, 12m, 9m, 11.5m, 500m);

        Assert.Equal(a, b);
        Assert.True(a == b);
        Assert.NotEqual(a, c);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
    }
}
