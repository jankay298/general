using System.Text.Json;
using Daytrading.Execution;
using Daytrading.Execution.Tests.TestSupport;

namespace Daytrading.Execution.Tests;

public class RiskLimitsTests
{
    [Fact]
    public void Default_limits_are_valid()
    {
        RiskLimits.Default.Validate();
    }

    [Fact]
    public void Rejects_a_per_trade_risk_larger_than_the_open_risk_budget()
    {
        var limits = RiskLimits.Default;
        limits.RiskPerTradePercent = 5m;

        var error = Assert.Throws<RiskConfigurationException>(() => limits.Validate());

        Assert.Contains("RiskPerTradePercent", error.Message);
    }

    [Fact]
    public void Rejects_an_open_risk_budget_larger_than_the_daily_drawdown_limit()
    {
        // Sonst wäre die Grenze für offenes Risiko wirkungslos: die Tagesgrenze griffe immer zuerst.
        var limits = RiskLimits.Default;
        limits.MaxOpenRiskPercent = 8m;

        var error = Assert.Throws<RiskConfigurationException>(() => limits.Validate());

        Assert.Contains("MaxDailyDrawdownPercent", error.Message);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(101)]
    public void Rejects_percentages_outside_the_plausible_range(decimal value)
    {
        var limits = RiskLimits.Default;
        limits.MaxTotalDrawdownPercent = value;

        Assert.Throws<RiskConfigurationException>(() => limits.Validate());
    }

    [Fact]
    public void Rejects_counts_below_one()
    {
        var noPositions = RiskLimits.Default;
        noPositions.MaxConcurrentPositions = 0;
        Assert.Throws<RiskConfigurationException>(() => noPositions.Validate());

        var noTrades = RiskLimits.Default;
        noTrades.MaxTradesPerDay = 0;
        Assert.Throws<RiskConfigurationException>(() => noTrades.Validate());
    }

    [Fact]
    public void Rejects_a_force_flat_window_of_zero()
    {
        var limits = RiskLimits.Default;
        limits.ForceFlatMinutesBeforeSessionEnd = 0;

        Assert.Throws<RiskConfigurationException>(() => limits.Validate());
    }

    [Fact]
    public void Rejects_a_maximum_holding_time_below_a_minute()
    {
        var limits = RiskLimits.Default;
        limits.MaxHoldingMinutes = 0;

        Assert.Throws<RiskConfigurationException>(() => limits.Validate());
    }

    /// <summary>
    /// Hält die Konfigurationsdatei und die Defaults im Code deckungsgleich. Ohne diesen Test
    /// könnten beide auseinanderlaufen, und niemand würde es merken, bis ein Backtest mit
    /// anderen Grenzen läuft als der Livebetrieb.
    /// </summary>
    [Fact]
    public void Code_defaults_match_the_configuration_file()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(RepositoryFile("config/risk.defaults.json")));
        var root = document.RootElement;
        var limits = RiskLimits.Default;

        Assert.Equal(limits.RiskPerTradePercent, root.GetProperty("riskPerTradePercent").GetDecimal());
        Assert.Equal(limits.MaxOpenRiskPercent, root.GetProperty("maxOpenRiskPercent").GetDecimal());
        Assert.Equal(limits.MaxDailyLossPercent, root.GetProperty("maxDailyLossPercent").GetDecimal());
        Assert.Equal(limits.MaxDailyDrawdownPercent, root.GetProperty("maxDailyDrawdownPercent").GetDecimal());
        Assert.Equal(limits.MaxTotalDrawdownPercent, root.GetProperty("maxTotalDrawdownPercent").GetDecimal());
        Assert.Equal(limits.MaxConcurrentPositions, root.GetProperty("maxConcurrentPositions").GetInt32());
        Assert.Equal(limits.MaxTradesPerDay, root.GetProperty("maxTradesPerDay").GetInt32());
        Assert.Equal(limits.CapRiskToRemainingDailyBudget, root.GetProperty("capRiskToRemainingDailyBudget").GetBoolean());
        Assert.Equal(
            limits.ForceFlatMinutesBeforeSessionEnd,
            root.GetProperty("forceFlatMinutesBeforeSessionEnd").GetInt32());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("maxHoldingMinutes").ValueKind);
        Assert.Null(limits.MaxHoldingMinutes);
    }

    [Fact]
    public void The_configuration_file_carries_no_keys_the_code_ignores()
    {
        // Fängt den Fall ab, dass jemand eine Grenze in die Datei schreibt, die niemand liest -
        // besonders die abgeschafften Schalter für Stop-Pflicht, Overnight und Nachkaufen.
        var known = new HashSet<string>
        {
            "_comment",
            "_not_configurable",
            "riskPerTradePercent",
            "maxOpenRiskPercent",
            "maxDailyLossPercent",
            "maxDailyDrawdownPercent",
            "maxTotalDrawdownPercent",
            "maxConcurrentPositions",
            "maxTradesPerDay",
            "capRiskToRemainingDailyBudget",
            "forceFlatMinutesBeforeSessionEnd",
            "maxHoldingMinutes",
        };

        using var document = JsonDocument.Parse(File.ReadAllText(RepositoryFile("config/risk.defaults.json")));

        var unknown = document.RootElement
            .EnumerateObject()
            .Select(property => property.Name)
            .Where(name => !known.Contains(name))
            .ToList();

        Assert.Empty(unknown);
    }

    private static string RepositoryFile(string relativePath)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null)
        {
            var candidate = Path.Combine(directory.FullName, relativePath);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException($"'{relativePath}' wurde oberhalb von {AppContext.BaseDirectory} nicht gefunden.");
    }
}
