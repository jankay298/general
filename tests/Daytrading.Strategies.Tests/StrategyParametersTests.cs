using System.Collections.Generic;
using System.Globalization;
using Daytrading.Strategies;
using Daytrading.Strategies.Library;

namespace Daytrading.Strategies.Tests;

public class StrategyParametersTests
{
    private static StrategyParameters From(params (string Key, string Value)[] values)
    {
        var dictionary = new List<KeyValuePair<string, string>>();
        foreach (var value in values)
        {
            dictionary.Add(new KeyValuePair<string, string>(value.Key, value.Value));
        }

        return new StrategyParameters(dictionary);
    }

    [Fact]
    public void Reads_values_case_insensitively()
    {
        var parameters = From(("openingrangeminutes", "45"));

        Assert.Equal(45, parameters.GetInt("OpeningRangeMinutes", 30));
    }

    [Fact]
    public void Uses_defaults_for_missing_and_empty_values()
    {
        var parameters = From(("AtrStopMultiple", "   "));

        Assert.Equal(1.5m, parameters.GetDecimal("AtrStopMultiple", 1.5m));
        Assert.Equal(14, parameters.GetInt("AtrPeriod", 14));
    }

    [Fact]
    public void Parses_decimals_with_invariant_culture()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            // Deutsches Zahlenformat aktiv: der Punkt muss trotzdem Dezimaltrennzeichen bleiben,
            // sonst hängt das Backtest-Ergebnis von der Ländereinstellung der Maschine ab.
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            var parameters = From(("TakeProfitR", "2.5"));

            Assert.Equal(2.5m, parameters.GetDecimal("TakeProfitR", 2m));
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Theory]
    [InlineData("zwei")]
    [InlineData("2,5")]
    [InlineData("")]
    public void Throws_on_unparsable_numbers_instead_of_falling_back(string raw)
    {
        var parameters = From(("TakeProfitR", raw));

        if (string.IsNullOrWhiteSpace(raw))
        {
            // Leer heißt "nicht gesetzt" - das ist der einzige Fall, in dem der Default greift.
            Assert.Equal(2m, parameters.GetDecimal("TakeProfitR", 2m));
        }
        else
        {
            Assert.Throws<StrategyParameterException>(() => parameters.GetDecimal("TakeProfitR", 2m));
        }
    }

    [Theory]
    [InlineData("true", true)]
    [InlineData("TRUE", true)]
    [InlineData("1", true)]
    [InlineData("ja", true)]
    [InlineData("false", false)]
    [InlineData("0", false)]
    [InlineData("nein", false)]
    public void Parses_booleans(string raw, bool expected)
    {
        var parameters = From(("OneTradePerDay", raw));

        Assert.Equal(expected, parameters.GetBool("OneTradePerDay", !expected));
    }

    [Fact]
    public void Parses_enums_and_lists_allowed_values_on_error()
    {
        var valid = From(("StopLossMode", "atrmultiple"));
        Assert.Equal(
            OpeningRangeStopMode.AtrMultiple,
            valid.GetEnum("StopLossMode", OpeningRangeStopMode.OppositeRangeSide));

        var invalid = From(("StopLossMode", "Trailing"));
        var error = Assert.Throws<StrategyParameterException>(
            () => invalid.GetEnum("StopLossMode", OpeningRangeStopMode.OppositeRangeSide));
        Assert.Contains("OppositeRangeSide", error.Message);
    }

    [Fact]
    public void Enforces_range_limits()
    {
        var parameters = From(("OpeningRangeMinutes", "0"));

        var error = Assert.Throws<StrategyParameterException>(
            () => parameters.GetInt("OpeningRangeMinutes", 30, min: 1, max: 480));

        Assert.Contains("Minimum", error.Message);
    }

    [Fact]
    public void Records_every_resolved_value_including_defaults()
    {
        var parameters = From(("OpeningRangeMinutes", "45"));

        parameters.GetInt("OpeningRangeMinutes", 30);
        parameters.GetDecimal("TakeProfitR", 2m);
        parameters.GetBool("OneTradePerDay", true);

        Assert.Equal("45", parameters.Resolved["OpeningRangeMinutes"]);
        Assert.Equal("2", parameters.Resolved["TakeProfitR"]);
        Assert.Equal("true", parameters.Resolved["OneTradePerDay"]);
        Assert.Equal("OneTradePerDay=true;OpeningRangeMinutes=45;TakeProfitR=2", parameters.Fingerprint);
    }

    [Fact]
    public void Reports_supplied_but_never_read_keys()
    {
        // Klassischer Tippfehler in der Konfiguration. Ohne diese Meldung liefe eine ganze
        // Backtest-Matrix mit stillschweigend anderen Parametern als gedacht.
        var parameters = From(("OpeningRangeMinuts", "45"), ("TakeProfitR", "2"));

        parameters.GetInt("OpeningRangeMinutes", 30);
        parameters.GetDecimal("TakeProfitR", 2m);

        Assert.Equal(new[] { "OpeningRangeMinuts" }, parameters.UnusedKeys);
    }
}
