using Daytrading.Data;
using Daytrading.Data.Config;
using Daytrading.Strategies.Model;

namespace Daytrading.Data.Tests;

public class UsEquityHolidayCalendarTests
{
    private static readonly IHolidayCalendar Calendar = new UsEquityHolidayCalendar();

    [Theory]
    [InlineData("2024-01-01")]   // Neujahr
    [InlineData("2024-01-15")]   // Martin Luther King Jr. Day
    [InlineData("2024-02-19")]   // Washington's Birthday
    [InlineData("2024-03-29")]   // Karfreitag
    [InlineData("2024-05-27")]   // Memorial Day
    [InlineData("2024-06-19")]   // Juneteenth
    [InlineData("2024-07-04")]   // Independence Day
    [InlineData("2024-09-02")]   // Labor Day
    [InlineData("2024-11-28")]   // Thanksgiving
    [InlineData("2024-12-25")]   // Weihnachten
    public void Knows_the_regular_market_holidays(string date)
    {
        Assert.True(Calendar.IsHoliday(DateTime.Parse(date)));
    }

    [Theory]
    [InlineData("2024-01-02")]
    [InlineData("2024-07-03")]   // verkürzter Tag, aber kein Feiertag
    [InlineData("2024-11-29")]   // Tag nach Thanksgiving, verkürzt aber offen
    public void Does_not_invent_holidays(string date)
    {
        Assert.False(Calendar.IsHoliday(DateTime.Parse(date)));
    }

    [Fact]
    public void Moves_a_weekend_holiday_to_the_adjacent_weekday()
    {
        // 4. Juli 2020 fiel auf einen Samstag, die Börse blieb am Freitag zu.
        Assert.True(Calendar.IsHoliday(new DateTime(2020, 7, 3)));
        Assert.False(Calendar.IsHoliday(new DateTime(2020, 7, 4)));

        // 25. Dezember 2022 war ein Sonntag, die Börse blieb am Montag zu.
        Assert.True(Calendar.IsHoliday(new DateTime(2022, 12, 26)));
    }

    [Fact]
    public void Juneteenth_only_counts_from_2022()
    {
        Assert.False(Calendar.IsHoliday(new DateTime(2021, 6, 18)));
        Assert.True(Calendar.IsHoliday(new DateTime(2022, 6, 20)));   // 19.6.2022 war ein Sonntag
    }
}

public class SymbolSessionCalendarTests
{
    private static SymbolConfig Equity() => new SymbolConfig
    {
        Name = "AAPL",
        AssetClass = AssetClass.Equity,
        TimeZone = "America/New_York",
        SessionStart = "09:30",
        SessionEnd = "16:00",
        HolidayCalendar = "us-equity",
        WorkingTimeframe = "M15",
    };

    private static SymbolConfig Crypto() => new SymbolConfig
    {
        Name = "BTCUSD",
        SourceSymbol = "BTCUSDT",
        AssetClass = AssetClass.Crypto,
        TimeZone = "UTC",
        SessionStart = "00:00",
        SessionEnd = "24:00",
        WorkingTimeframe = "M5",
        TickSize = 0.01m,
        MinQuantity = 0.00001m,
        QuantityStep = 0.00001m,
    };

    [Fact]
    public void Converts_the_exchange_session_into_utc_and_follows_daylight_saving()
    {
        var calendar = new SymbolSessionCalendar(Equity());

        Assert.True(calendar.TryGetSession(new DateTime(2024, 2, 1), out var winter));
        Assert.Equal(new DateTime(2024, 2, 1, 14, 30, 0, DateTimeKind.Utc), winter.StartUtc);   // EST = UTC-5

        Assert.True(calendar.TryGetSession(new DateTime(2024, 7, 1), out var summer));
        Assert.Equal(new DateTime(2024, 7, 1, 13, 30, 0, DateTimeKind.Utc), summer.StartUtc);   // EDT = UTC-4
    }

    [Fact]
    public void Skips_weekends_and_holidays()
    {
        var calendar = new SymbolSessionCalendar(Equity());

        Assert.False(calendar.IsTradingDay(new DateTime(2024, 3, 2)));    // Samstag
        Assert.False(calendar.IsTradingDay(new DateTime(2024, 3, 29)));   // Karfreitag
        Assert.True(calendar.IsTradingDay(new DateTime(2024, 3, 28)));
    }

    [Fact]
    public void Finds_the_session_that_contains_a_given_instant()
    {
        var calendar = new SymbolSessionCalendar(Equity());

        Assert.True(calendar.TryGetSessionAt(new DateTime(2024, 7, 1, 15, 0, 0, DateTimeKind.Utc), out var session));
        Assert.Equal(new DateTime(2024, 7, 1, 0, 0, 0, DateTimeKind.Utc), session.TradingDay);

        // Vorbörslich: kein Handel, also keine Session.
        Assert.False(calendar.TryGetSessionAt(new DateTime(2024, 7, 1, 12, 0, 0, DateTimeKind.Utc), out _));
    }

    [Fact]
    public void Treats_the_crypto_day_as_a_full_utc_day_including_weekends()
    {
        var calendar = new SymbolSessionCalendar(Crypto());

        Assert.True(calendar.IsTradingDay(new DateTime(2024, 3, 2)));   // Samstag
        Assert.True(calendar.TryGetSession(new DateTime(2024, 3, 2), out var session));
        Assert.Equal(new DateTime(2024, 3, 2, 0, 0, 0, DateTimeKind.Utc), session.StartUtc);
        Assert.Equal(new DateTime(2024, 3, 3, 0, 0, 0, DateTimeKind.Utc), session.EndUtc);
    }

    [Fact]
    public void Enumerates_the_sessions_of_a_period()
    {
        var calendar = new SymbolSessionCalendar(Equity());

        var sessions = calendar.Sessions(
            new DateTime(2024, 3, 25, 0, 0, 0, DateTimeKind.Utc),
            new DateTime(2024, 4, 1, 0, 0, 0, DateTimeKind.Utc)).ToList();

        // Mo-Do plus Karfreitag geschlossen, Wochenende geschlossen -> vier Handelstage.
        Assert.Equal(4, sessions.Count);
        Assert.All(sessions, session => Assert.Equal(TimeSpan.FromHours(6.5), session.Length));
    }

    [Fact]
    public void Refuses_a_session_that_would_cross_midnight()
    {
        var config = Equity();
        config.SessionStart = "22:00";
        config.SessionEnd = "06:00";

        var error = Assert.Throws<MarketDataException>(() => new SymbolSessionCalendar(config));

        Assert.Contains("Mitternacht", error.Message);
    }
}

public class SymbolCatalogTests
{
    private const string Json = """
    [
      {
        "name": "BTCUSD",
        "sourceSymbol": "BTCUSDT",
        "assetClass": "Crypto",
        "timeZone": "UTC",
        "sessionStart": "00:00",
        "sessionEnd": "24:00",
        "workingTimeframe": "M5",
        "tickSize": 0.01,
        "minQuantity": 0.00001,
        "quantityStep": 0.00001,
        "typicalSpread": 2.5
      }
    ]
    """;

    [Fact]
    public void Reads_a_symbol_from_json()
    {
        var catalog = SymbolCatalog.Parse(Json);

        var symbol = catalog["BTCUSD"];
        Assert.Equal(AssetClass.Crypto, symbol.AssetClass);
        Assert.Equal("BTCUSDT", symbol.SourceName);
        Assert.Equal(Timeframe.M5, symbol.ToTimeframe());
        Assert.Equal(2.5m, symbol.TypicalSpread);
    }

    [Fact]
    public void Falls_back_to_the_internal_name_when_no_source_symbol_is_given()
    {
        var catalog = SymbolCatalog.Parse(Json.Replace("\"sourceSymbol\": \"BTCUSDT\",", string.Empty));

        Assert.Equal("BTCUSD", catalog["BTCUSD"].SourceName);
    }

    [Fact]
    public void Rejects_an_unknown_symbol_by_name()
    {
        var catalog = SymbolCatalog.Parse(Json);

        Assert.Throws<MarketDataException>(() => catalog["ETHUSD"]);
    }

    [Fact]
    public void Rejects_duplicates_and_broken_definitions()
    {
        const string duplicated = """
        [
          { "name": "BTCUSD", "assetClass": "Crypto" },
          { "name": "btcusd", "assetClass": "Crypto" }
        ]
        """;

        var error = Assert.Throws<MarketDataException>(() => SymbolCatalog.Parse(duplicated));
        Assert.Contains("doppelt", error.Message);

        Assert.Throws<MarketDataException>(() => SymbolCatalog.Parse("[]"));
        Assert.Throws<MarketDataException>(() => SymbolCatalog.Parse("kein json"));
    }

    [Fact]
    public void Rejects_a_negative_spread_and_an_unknown_timezone()
    {
        var negativeSpread = new SymbolConfig { Name = "X", TypicalSpread = -1m };
        Assert.Throws<MarketDataException>(() => negativeSpread.Validate());

        var unknownZone = new SymbolConfig { Name = "X", TimeZone = "Mars/Olympus" };
        Assert.Throws<MarketDataException>(() => unknownZone.Validate());
    }
}
