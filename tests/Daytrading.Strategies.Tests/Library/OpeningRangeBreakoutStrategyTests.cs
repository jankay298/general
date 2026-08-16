using System;
using System.Collections.Generic;
using System.Linq;
using Daytrading.Strategies;
using Daytrading.Strategies.Library;
using Daytrading.Strategies.Model;
using Daytrading.Strategies.Tests.TestSupport;

namespace Daytrading.Strategies.Tests.Library;

/// <summary>
/// Tests der Beispielstrategie. Sie laufen ohne cTrader, ohne Broker und ohne Datei -
/// genau das ist der Zweck der Entkopplung.
/// </summary>
public class OpeningRangeBreakoutStrategyTests
{
    private static readonly DateTime Day1 = TestMarket.Utc(2024, 3, 1);
    private static readonly DateTime Day2 = TestMarket.Utc(2024, 3, 4);

    private static DateTime SessionStart(DateTime day) => day.AddHours(13).AddMinutes(30);

    private static StrategyHarness Harness(params (string Key, string Value)[] parameters)
    {
        var values = parameters
            .Select(pair => new KeyValuePair<string, string>(pair.Key, pair.Value))
            .ToList();

        return new StrategyHarness(
            new OpeningRangeBreakoutStrategy(),
            TestMarket.Equity(),
            Timeframe.M15,
            TestMarket.UsEquitySession,
            values);
    }

    /// <summary>
    /// Standardtag: 13:30-13:45 und 13:45-14:00 bilden die 30-Minuten-Range
    /// [99.00, 101.50]; ab 14:00 wird auf Ausbruch geprüft.
    /// </summary>
    private static List<Candle> DayWithBreakout(DateTime day, params (decimal High, decimal Low, decimal Close)[] afterRange)
    {
        var bars = new (decimal High, decimal Low, decimal Close)[2 + afterRange.Length];
        bars[0] = (101m, 99m, 100.5m);
        bars[1] = (101.5m, 99.5m, 100m);
        Array.Copy(afterRange, 0, bars, 2, afterRange.Length);

        return TestMarket.Series(SessionStart(day), Timeframe.M15, bars);
    }

    [Fact]
    public void Emits_a_long_signal_on_the_first_close_above_the_opening_range()
    {
        var harness = Harness();

        var signals = harness.Run(DayWithBreakout(Day1, (102.5m, 100m, 102m)));

        var signal = Assert.Single(signals);
        Assert.Equal(SessionStart(Day1).AddMinutes(45), signal.BarCloseUtc);
        Assert.Equal(TradeDirection.Long, signal.Signal.Direction);
        Assert.Equal(102m, signal.Signal.ReferencePrice);
        Assert.Equal(99m, signal.Signal.StopLoss);       // Gegenseite der Range
        Assert.Equal(108m, signal.Signal.TakeProfit);    // 2R bei 3.00 Stopabstand
        Assert.Equal(2m, signal.Signal.RewardRiskRatio);
        Assert.Contains("ORB long", signal.Signal.Reason);
    }

    [Fact]
    public void Emits_a_short_signal_on_the_first_close_below_the_opening_range()
    {
        var harness = Harness();

        var signals = harness.Run(DayWithBreakout(Day1, (100m, 97.5m, 98m)));

        var signal = Assert.Single(signals);
        Assert.Equal(TradeDirection.Short, signal.Signal.Direction);
        Assert.Equal(101.5m, signal.Signal.StopLoss);
        Assert.Equal(91m, signal.Signal.TakeProfit);     // 98 - 2 * 3.50
    }

    [Fact]
    public void Never_signals_while_the_opening_range_is_still_forming()
    {
        // Die zweite Bar schließt weit über der ersten - trotzdem darf kein Signal entstehen,
        // solange das Eröffnungsfenster läuft.
        var bars = TestMarket.Series(
            SessionStart(Day1),
            Timeframe.M15,
            (101m, 99m, 100.5m),
            (110m, 100m, 109m),
            (108m, 105m, 106m));

        var signals = Harness().Run(bars);

        Assert.Empty(signals);
    }

    [Fact]
    public void Signals_only_once_per_day_by_default()
    {
        var harness = Harness();

        var signals = harness.Run(DayWithBreakout(
            Day1,
            (102.5m, 100m, 102m),
            (104m, 102m, 103.5m),
            (105m, 103m, 104.5m)));

        Assert.Single(signals);
    }

    [Fact]
    public void Signals_repeatedly_when_one_trade_per_day_is_disabled()
    {
        var harness = Harness(("OneTradePerDay", "false"));

        var signals = harness.Run(DayWithBreakout(
            Day1,
            (102.5m, 100m, 102m),
            (104m, 102m, 103.5m)));

        Assert.Equal(2, signals.Count);
    }

    [Fact]
    public void Does_not_signal_while_a_position_is_open()
    {
        var harness = Harness(("OneTradePerDay", "false"));
        harness.OpenPositions.Add(new Position(
            "1", "TEST", TradeDirection.Long, SessionStart(Day1), 100m, 98m, null, 10m, "ORB"));

        var signals = harness.Run(DayWithBreakout(Day1, (102.5m, 100m, 102m)));

        Assert.Empty(signals);
    }

    [Fact]
    public void Builds_a_fresh_opening_range_on_the_next_trading_day()
    {
        var harness = Harness();
        harness.Run(DayWithBreakout(Day1, (102.5m, 100m, 102m)));

        harness.Run(DayWithBreakout(Day2, (102.5m, 100m, 102m)));

        Assert.Equal(2, harness.Signals.Count);
        Assert.Equal(SessionStart(Day1).AddMinutes(45), harness.Signals[0].BarCloseUtc);
        Assert.Equal(SessionStart(Day2).AddMinutes(45), harness.Signals[1].BarCloseUtc);
    }

    [Fact]
    public void Requires_the_breakout_to_clear_the_configured_buffer()
    {
        // Puffer 10 Ticks = 0.10; Range-Hoch 101.50, also erst ab 101.60 ein Signal.
        var tooSmall = Harness(("BreakoutBufferTicks", "10"))
            .Run(DayWithBreakout(Day1, (101.7m, 100m, 101.55m)));
        Assert.Empty(tooSmall);

        var clear = Harness(("BreakoutBufferTicks", "10"))
            .Run(DayWithBreakout(Day1, (101.8m, 100m, 101.65m)));
        Assert.Single(clear);
    }

    [Fact]
    public void Ignores_bars_before_the_session_start()
    {
        // Vorbörsliche Bar mit extremem Hoch: Sie darf die Eröffnungsrange nicht aufblähen,
        // sonst käme das Signal nie oder mit falschem Stop.
        var preMarket = TestMarket.Series(
            SessionStart(Day1).AddHours(-1),
            Timeframe.M15,
            (200m, 150m, 180m));
        var harness = Harness();

        harness.Run(preMarket);
        var signals = harness.Run(DayWithBreakout(Day1, (102.5m, 100m, 102m)));

        var signal = Assert.Single(signals);
        Assert.Equal(99m, signal.Signal.StopLoss);
    }

    [Fact]
    public void Uses_the_atr_for_the_stop_when_configured()
    {
        var harness = Harness(
            ("StopLossMode", "AtrMultiple"),
            ("AtrPeriod", "2"),
            ("AtrStopMultiple", "1.5"));

        var signals = harness.Run(DayWithBreakout(Day1, (102.5m, 100m, 102m)));

        // TR der drei Bars: 2.00, 2.00, 2.50 -> ATR(2) = 2.25 -> Stop = 102 - 3.375, auf Tick abgerundet.
        var signal = Assert.Single(signals);
        Assert.Equal(98.62m, signal.Signal.StopLoss);
        Assert.Equal(108.76m, signal.Signal.TakeProfit);
    }

    [Fact]
    public void Waits_for_the_atr_warmup_before_signalling()
    {
        var harness = Harness(
            ("StopLossMode", "AtrMultiple"),
            ("AtrPeriod", "20"),
            ("AtrStopMultiple", "1.5"));

        var signals = harness.Run(DayWithBreakout(Day1, (102.5m, 100m, 102m)));

        Assert.Empty(signals);
        Assert.Contains(harness.Log.Entries, entry => entry.Contains("ATR(20)"));
    }

    [Fact]
    public void Skips_the_day_when_no_bar_fits_into_the_opening_window()
    {
        // 10-Minuten-Fenster bei 15-Minuten-Bars: keine Bar liegt vollständig darin.
        var harness = Harness(("OpeningRangeMinutes", "10"));

        var signals = harness.Run(DayWithBreakout(Day1, (102.5m, 100m, 102m)));

        Assert.Empty(signals);
        Assert.Contains(harness.Log.Entries, entry => entry.Contains("Eröffnungsfenster"));
    }

    [Fact]
    public void Honours_the_direction_filter()
    {
        var harness = Harness(("AllowShort", "false"));

        var signals = harness.Run(DayWithBreakout(Day1, (100m, 97.5m, 98m)));

        Assert.Empty(signals);
    }

    [Fact]
    public void Rejects_a_configuration_that_could_never_trade()
    {
        var error = Assert.Throws<StrategyParameterException>(
            () => Harness(("AllowLong", "false"), ("AllowShort", "false")));

        Assert.Contains("AllowLong", error.Message);
    }

    [Fact]
    public void Reports_its_warmup_requirement()
    {
        var withoutAtr = new OpeningRangeBreakoutStrategy();
        withoutAtr.Initialize(new StrategyContext(TestMarket.Equity(), Timeframe.M15));
        Assert.Equal(0, withoutAtr.WarmupBars);

        var withAtr = new OpeningRangeBreakoutStrategy();
        withAtr.Initialize(new StrategyContext(
            TestMarket.Equity(),
            Timeframe.M15,
            new StrategyParameters(new[]
            {
                new KeyValuePair<string, string>("StopLossMode", "AtrMultiple"),
                new KeyValuePair<string, string>("AtrPeriod", "14"),
            })));
        Assert.Equal(15, withAtr.WarmupBars);
    }

    [Fact]
    public void Fails_loudly_when_used_without_initialize()
    {
        var series = new BarSeries();
        series.Append(TestMarket.Bar(SessionStart(Day1), 100m));
        var snapshot = new MarketSnapshot(
            TestMarket.Equity(), Timeframe.M15, series, TestMarket.UsEquitySession(Day1));

        var error = Assert.Throws<InvalidOperationException>(() => new OpeningRangeBreakoutStrategy().OnBar(snapshot));

        Assert.Contains("Initialize", error.Message);
    }

    [Fact]
    public void Produces_identical_signals_for_identical_input()
    {
        var bars = DayWithBreakout(Day1, (102.5m, 100m, 102m), (104m, 102m, 103.5m));

        var first = Harness(("OneTradePerDay", "false")).Run(bars);
        var second = Harness(("OneTradePerDay", "false")).Run(bars);

        Assert.Equal(first.Count, second.Count);
        for (var i = 0; i < first.Count; i++)
        {
            Assert.Equal(first[i].BarCloseUtc, second[i].BarCloseUtc);
            Assert.Equal(first[i].Signal.Direction, second[i].Signal.Direction);
            Assert.Equal(first[i].Signal.ReferencePrice, second[i].Signal.ReferencePrice);
            Assert.Equal(first[i].Signal.StopLoss, second[i].Signal.StopLoss);
            Assert.Equal(first[i].Signal.TakeProfit, second[i].Signal.TakeProfit);
            Assert.Equal(first[i].Signal.Reason, second[i].Signal.Reason);
        }
    }

    [Fact]
    public void Records_every_parameter_it_used()
    {
        var harness = Harness(("OpeningRangeMinutes", "45"));

        Assert.Equal("45", harness.Parameters.Resolved["OpeningRangeMinutes"]);
        Assert.Contains("StopLossMode=OppositeRangeSide", harness.Parameters.Fingerprint);
        Assert.Empty(harness.Parameters.UnusedKeys);
    }
}
