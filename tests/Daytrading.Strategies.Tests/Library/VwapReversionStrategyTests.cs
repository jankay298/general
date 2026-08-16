using System;
using System.Collections.Generic;
using System.Linq;
using Daytrading.Strategies;
using Daytrading.Strategies.Library;
using Daytrading.Strategies.Model;
using Daytrading.Strategies.Tests.TestSupport;

namespace Daytrading.Strategies.Tests.Library;

public class VwapReversionStrategyTests
{
    private static readonly DateTime Day1 = TestMarket.Utc(2024, 3, 1);
    private static readonly DateTime Day2 = TestMarket.Utc(2024, 3, 4);

    private static DateTime SessionStart(DateTime day) => day.AddHours(13).AddMinutes(30);

    private static StrategyHarness Harness(params (string Key, string Value)[] parameters)
    {
        var values = new List<KeyValuePair<string, string>>
        {
            new("MinBarsForVwap", "3"),
            new("AtrPeriod", "3"),
            new("BandSigma", "1"),
        };

        values.AddRange(parameters.Select(pair => new KeyValuePair<string, string>(pair.Key, pair.Value)));

        return new StrategyHarness(
            new VwapReversionStrategy(),
            TestMarket.Equity(),
            Timeframe.M15,
            TestMarket.UsEquitySession,
            values);
    }

    /// <summary>
    /// Vier ruhige Bars auf 100 - dort ist die Streuung null, es kann also kein Signal geben -
    /// und danach ein Einbruch weit unter das Band.
    /// </summary>
    private static List<Candle> DayWithDrop(DateTime day) => TestMarket.Series(
        SessionStart(day),
        Timeframe.M15,
        (100.5m, 99.5m, 100m),
        (100.5m, 99.5m, 100m),
        (100.5m, 99.5m, 100m),
        (100.5m, 99.5m, 100m),
        (95m, 93.5m, 94m));

    [Fact]
    public void Waits_until_enough_bars_of_the_session_have_formed()
    {
        var bars = DayWithDrop(Day1).Take(2).ToList();

        Assert.Empty(Harness().Run(bars));
    }

    [Fact]
    public void Buys_below_the_lower_band_and_targets_the_vwap()
    {
        var signals = Harness().Run(DayWithDrop(Day1));

        var signal = Assert.Single(signals);
        Assert.Equal(SignalKind.Entry, signal.Signal.Kind);
        Assert.Equal(TradeDirection.Long, signal.Signal.Direction);
        Assert.Equal(94m, signal.Signal.ReferencePrice);
        Assert.True(signal.Signal.StopLoss < 94m, "Der Stop muss unter dem Einstieg liegen.");
        Assert.True(signal.Signal.TakeProfit > 94m, "Das Ziel ist der VWAP und liegt über dem Einstieg.");
        Assert.Contains("VWAP-Reversion", signal.Signal.Reason);
    }

    [Fact]
    public void Sells_above_the_upper_band()
    {
        var bars = TestMarket.Series(
            SessionStart(Day1),
            Timeframe.M15,
            (100.5m, 99.5m, 100m),
            (100.5m, 99.5m, 100m),
            (100.5m, 99.5m, 100m),
            (100.5m, 99.5m, 100m),
            (107m, 105.5m, 106m));

        var signals = Harness().Run(bars);

        var signal = Assert.Single(signals);
        Assert.Equal(TradeDirection.Short, signal.Signal.Direction);
        Assert.True(signal.Signal.StopLoss > 106m);
        Assert.True(signal.Signal.TakeProfit < 106m);
    }

    [Fact]
    public void Asks_to_close_when_the_price_returns_to_the_vwap()
    {
        var harness = Harness();
        harness.Run(DayWithDrop(Day1));

        // Position offen, Kurs kehrt zum Mittelwert zurück.
        harness.OpenPositions.Add(new Position(
            "1", "TEST", TradeDirection.Long, SessionStart(Day1), 94m, 92m, null, 10m, "VWAP"));

        var signal = harness.RunSingle(TestMarket.Bar(SessionStart(Day1).AddMinutes(75), 100m));

        Assert.NotNull(signal);
        Assert.Equal(SignalKind.CloseAll, signal!.Kind);
        Assert.Null(signal.Direction);
        Assert.Contains("VWAP erreicht", signal.Reason);
    }

    [Fact]
    public void Holds_while_the_price_has_not_reached_the_vwap_yet()
    {
        var harness = Harness();
        harness.Run(DayWithDrop(Day1));
        harness.OpenPositions.Add(new Position(
            "1", "TEST", TradeDirection.Long, SessionStart(Day1), 94m, 92m, null, 10m, "VWAP"));

        var signal = harness.RunSingle(TestMarket.Bar(SessionStart(Day1).AddMinutes(75), 95m));

        Assert.Null(signal);
    }

    [Fact]
    public void Respects_the_maximum_number_of_entries_per_day()
    {
        var harness = Harness(("MaxEntriesPerDay", "1"));
        var bars = DayWithDrop(Day1);
        bars.Add(TestMarket.Bar(SessionStart(Day1).AddMinutes(75), 99m));
        bars.Add(TestMarket.Bar(SessionStart(Day1).AddMinutes(90), 90m));

        var signals = harness.Run(bars);

        Assert.Single(signals);
    }

    [Fact]
    public void Starts_a_fresh_vwap_on_the_next_trading_day()
    {
        var harness = Harness();
        harness.Run(DayWithDrop(Day1));

        harness.Run(DayWithDrop(Day2));

        Assert.Equal(2, harness.Signals.Count);
        Assert.Equal(Day1.Date, harness.Signals[0].BarCloseUtc.Date);
        Assert.Equal(Day2.Date, harness.Signals[1].BarCloseUtc.Date);
    }

    [Fact]
    public void Reports_missing_volume_instead_of_quietly_computing_something_else()
    {
        // Manche CFD-Quellen liefern kein Volumen. Dann ist es kein VWAP mehr - und das gehört gesagt.
        var harness = Harness();
        var bars = DayWithDrop(Day1)
            .Select(bar => new Candle(bar.OpenTimeUtc, bar.Open, bar.High, bar.Low, bar.Close, 0m))
            .ToList();

        harness.Run(bars);

        Assert.Contains(harness.Log.Entries, entry => entry.Contains("kein Volumen"));
    }

    [Fact]
    public void Produces_identical_signals_for_identical_input()
    {
        var bars = DayWithDrop(Day1);

        var first = Harness().Run(bars);
        var second = Harness().Run(bars);

        Assert.Equal(first.Count, second.Count);
        for (var i = 0; i < first.Count; i++)
        {
            Assert.Equal(first[i].Signal.ReferencePrice, second[i].Signal.ReferencePrice);
            Assert.Equal(first[i].Signal.StopLoss, second[i].Signal.StopLoss);
            Assert.Equal(first[i].Signal.TakeProfit, second[i].Signal.TakeProfit);
        }
    }

    [Fact]
    public void Refuses_a_configuration_that_could_never_trade()
    {
        Assert.Throws<StrategyParameterException>(
            () => Harness(("AllowLong", "false"), ("AllowShort", "false")));
    }

    [Fact]
    public void Fails_loudly_when_used_without_initialize()
    {
        var series = new BarSeries();
        series.Append(TestMarket.Bar(SessionStart(Day1), 100m));
        var snapshot = new MarketSnapshot(
            TestMarket.Equity(), Timeframe.M15, series, TestMarket.UsEquitySession(Day1));

        Assert.Throws<InvalidOperationException>(() => new VwapReversionStrategy().OnBar(snapshot));
    }
}
