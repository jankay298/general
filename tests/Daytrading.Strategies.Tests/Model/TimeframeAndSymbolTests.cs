using System;
using Daytrading.Strategies.Model;

namespace Daytrading.Strategies.Tests.Model;

public class TimeframeTests
{
    [Theory]
    [InlineData("M1", 1)]
    [InlineData("M15", 15)]
    [InlineData("15m", 15)]
    [InlineData("H1", 60)]
    [InlineData("1h", 60)]
    [InlineData("D1", 1440)]
    public void Parses_common_notations(string text, int expectedMinutes)
    {
        var timeframe = Timeframe.Parse(text);

        Assert.Equal(TimeSpan.FromMinutes(expectedMinutes), timeframe.Duration);
    }

    [Theory]
    [InlineData("")]
    [InlineData("X5")]
    [InlineData("M0")]
    [InlineData("M-5")]
    [InlineData("fünfzehn")]
    public void Rejects_unknown_notations(string text)
    {
        Assert.False(Timeframe.TryParse(text, out _));
        Assert.Throws<FormatException>(() => Timeframe.Parse(text));
    }

    [Fact]
    public void Round_trips_through_its_canonical_form()
    {
        foreach (var timeframe in new[] { Timeframe.M1, Timeframe.M5, Timeframe.M15, Timeframe.H1, Timeframe.H4, Timeframe.D1 })
        {
            Assert.Equal(timeframe, Timeframe.Parse(timeframe.ToString()));
        }
    }

    [Fact]
    public void Rejects_non_positive_duration()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new Timeframe(TimeSpan.Zero));
        Assert.Throws<ArgumentOutOfRangeException>(() => new Timeframe(TimeSpan.FromMinutes(-5)));
    }
}

public class SymbolInfoTests
{
    [Fact]
    public void Rounds_prices_to_the_tick_grid()
    {
        var symbol = new SymbolInfo("XAUUSD", AssetClass.Commodity, 0.05m);

        Assert.Equal(2010.15m, symbol.RoundToTick(2010.16m));
        Assert.Equal(2010.15m, symbol.RoundDownToTick(2010.19m));
        Assert.Equal(2010.20m, symbol.RoundUpToTick(2010.16m));
    }

    [Fact]
    public void Rejects_invalid_definitions()
    {
        Assert.Throws<ArgumentException>(() => new SymbolInfo(" ", AssetClass.Equity, 0.01m));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SymbolInfo("AAPL", AssetClass.Equity, 0m));
    }
}

public class TradingSessionTests
{
    [Fact]
    public void Reports_position_within_the_session()
    {
        var day = new DateTime(2024, 3, 1, 0, 0, 0, DateTimeKind.Utc);
        var session = new TradingSession(day, day.AddHours(13.5), day.AddHours(20));

        Assert.False(session.Contains(day.AddHours(13)));
        Assert.True(session.Contains(day.AddHours(14)));
        Assert.False(session.Contains(day.AddHours(20)));  // Ende ist exklusiv
        Assert.Equal(TimeSpan.FromMinutes(30), session.Elapsed(day.AddHours(14)));
        Assert.Equal(TimeSpan.FromHours(6), session.Remaining(day.AddHours(14)));
        Assert.Equal(TimeSpan.FromHours(6.5), session.Length);
    }

    [Fact]
    public void Rejects_inconsistent_definitions()
    {
        var day = new DateTime(2024, 3, 1, 0, 0, 0, DateTimeKind.Utc);

        Assert.Throws<ArgumentException>(() => new TradingSession(day.AddHours(1), day, day.AddHours(20)));
        Assert.Throws<ArgumentException>(() => new TradingSession(day, day.AddHours(20), day.AddHours(13)));
        Assert.Throws<ArgumentException>(
            () => new TradingSession(DateTime.SpecifyKind(day, DateTimeKind.Local), day, day.AddHours(20)));
    }
}
