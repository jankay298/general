using System;
using System.Linq;
using Daytrading.Strategies;

namespace Daytrading.Strategies.Tests;

public class StrategyCatalogTests
{
    [Theory]
    [InlineData("OpeningRangeBreakout")]
    [InlineData("openingrangebreakout")]
    [InlineData("VwapReversion")]
    public void Creates_the_known_strategies_by_name_regardless_of_case(string name)
    {
        var strategy = StrategyCatalog.Create(name);

        Assert.NotNull(strategy);
        Assert.False(string.IsNullOrWhiteSpace(strategy.Descriptor.Version));
    }

    [Fact]
    public void Each_call_returns_a_fresh_instance()
    {
        // Zustand darf nicht zwischen Läufen oder Konten lecken.
        Assert.NotSame(StrategyCatalog.Create("VwapReversion"), StrategyCatalog.Create("VwapReversion"));
    }

    [Fact]
    public void An_unknown_name_lists_the_known_ones()
    {
        var error = Assert.Throws<ArgumentException>(() => StrategyCatalog.Create("Wunschdenken"));

        Assert.Contains("OpeningRangeBreakout", error.Message);
        Assert.Contains("VwapReversion", error.Message);
    }

    [Fact]
    public void Names_are_listed_in_a_stable_order()
    {
        Assert.Equal(StrategyCatalog.Names, StrategyCatalog.Names.OrderBy(name => name, StringComparer.Ordinal));
    }

    [Fact]
    public void Reads_parameters_from_a_single_line()
    {
        // So lassen sich Strategieparameter über ein einziges cTrader-Eingabefeld setzen.
        var parameters = StrategyCatalog.ParseParameters("OpeningRangeMinutes=45; TakeProfitR=2.5");

        Assert.Equal(45, parameters.GetInt("OpeningRangeMinutes", 30));
        Assert.Equal(2.5m, parameters.GetDecimal("TakeProfitR", 2m));
    }

    [Fact]
    public void An_empty_line_means_defaults()
    {
        var parameters = StrategyCatalog.ParseParameters("   ");

        Assert.Equal(30, parameters.GetInt("OpeningRangeMinutes", 30));
        Assert.Empty(parameters.UnusedKeys);
    }

    [Fact]
    public void A_malformed_entry_is_reported_instead_of_ignored()
    {
        var error = Assert.Throws<StrategyParameterException>(
            () => StrategyCatalog.ParseParameters("OpeningRangeMinutes 45"));

        Assert.Contains("Name=Wert", error.Message);
    }
}
