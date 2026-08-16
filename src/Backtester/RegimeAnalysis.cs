using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Daytrading.Execution;
using Daytrading.Strategies.Model;

namespace Daytrading.Backtester;

public enum VolatilityRegime
{
    Low = 0,
    Normal = 1,
    High = 2,
}

public enum TrendRegime
{
    Down = 0,
    Sideways = 1,
    Up = 2,
}

/// <summary>Marktbedingungen eines Handelstages.</summary>
public sealed class DayRegime
{
    public DayRegime(DateTime day, decimal rangePercent, decimal returnPercent, VolatilityRegime volatility, TrendRegime trend)
    {
        Day = day;
        RangePercent = rangePercent;
        ReturnPercent = returnPercent;
        Volatility = volatility;
        Trend = trend;
    }

    public DateTime Day { get; }

    public decimal RangePercent { get; }

    public decimal ReturnPercent { get; }

    public VolatilityRegime Volatility { get; }

    public TrendRegime Trend { get; }
}

/// <summary>Ergebnis einer Strategie unter bestimmten Marktbedingungen.</summary>
public sealed class RegimeResult
{
    public string Symbol { get; init; } = string.Empty;

    public string StrategyKey { get; init; } = string.Empty;

    public VolatilityRegime Volatility { get; init; }

    public TrendRegime Trend { get; init; }

    /// <summary>Stunde des Einstiegs in UTC. -1 steht für "über alle Stunden".</summary>
    public int HourUtc { get; init; }

    public int TradeCount { get; init; }

    public decimal NetPnL { get; init; }

    public decimal ExpectancyR { get; init; }

    public decimal HitRatePercent { get; init; }
}

/// <summary>
/// Ordnet jedem Handelstag Volatilität und Trendrichtung zu und wertet die Trades danach aus.
/// </summary>
/// <remarks>
/// Daraus entsteht die eigentliche Erkenntnis: nicht "Strategie A ist gut", sondern
/// "Strategie A funktioniert auf Gold in volatilen Vormittagsphasen und verliert im ruhigen
/// Nachmittag".
///
/// Die Schwellen für hoch, normal und niedrig sind Terzile <b>über den gesamten Zeitraum</b>.
/// Das ist ausdrücklich eine Auswertung im Nachhinein und fließt an keiner Stelle in eine
/// Handelsentscheidung ein - die Strategie sieht diese Einteilung nie, es entsteht also kein
/// Look-ahead in den Ergebnissen.
/// </remarks>
public static class RegimeAnalysis
{
    public static IReadOnlyDictionary<DateTime, DayRegime> TagDays(IReadOnlyList<Candle> bars)
    {
        if (bars == null)
        {
            throw new ArgumentNullException(nameof(bars));
        }

        var days = bars
            .GroupBy(bar => bar.OpenTimeUtc.Date)
            .OrderBy(group => group.Key)
            .Select(group =>
            {
                var ordered = group.OrderBy(bar => bar.OpenTimeUtc).ToList();
                var high = ordered.Max(bar => bar.High);
                var low = ordered.Min(bar => bar.Low);
                var open = ordered[0].Open;
                var close = ordered[^1].Close;
                var rangePercent = open <= 0m ? 0m : (high - low) / open * 100m;
                var returnPercent = open <= 0m ? 0m : (close - open) / open * 100m;
                return (Day: group.Key, RangePercent: rangePercent, ReturnPercent: returnPercent);
            })
            .ToList();

        if (days.Count == 0)
        {
            return new Dictionary<DateTime, DayRegime>();
        }

        var sortedRanges = days.Select(day => day.RangePercent).OrderBy(value => value).ToList();
        var lowCut = Percentile(sortedRanges, 1m / 3m);
        var highCut = Percentile(sortedRanges, 2m / 3m);

        var absoluteReturns = days.Select(day => Math.Abs(day.ReturnPercent)).OrderBy(value => value).ToList();
        var sidewaysCut = Percentile(absoluteReturns, 1m / 3m);

        var result = new Dictionary<DateTime, DayRegime>(days.Count);
        foreach (var day in days)
        {
            var volatility = day.RangePercent <= lowCut
                ? VolatilityRegime.Low
                : day.RangePercent >= highCut ? VolatilityRegime.High : VolatilityRegime.Normal;

            var trend = Math.Abs(day.ReturnPercent) <= sidewaysCut
                ? TrendRegime.Sideways
                : day.ReturnPercent > 0m ? TrendRegime.Up : TrendRegime.Down;

            result[day.Day] = new DayRegime(day.Day, day.RangePercent, day.ReturnPercent, volatility, trend);
        }

        return result;
    }

    /// <summary>Ergebnis je Regime, wahlweise zusätzlich nach Einstiegsstunde aufgeschlüsselt.</summary>
    public static IReadOnlyList<RegimeResult> Evaluate(
        BacktestResult backtest,
        IReadOnlyDictionary<DateTime, DayRegime> regimes,
        bool byHour = false)
    {
        if (backtest == null)
        {
            throw new ArgumentNullException(nameof(backtest));
        }

        if (regimes == null)
        {
            throw new ArgumentNullException(nameof(regimes));
        }

        var rows = new List<RegimeResult>();

        var groups = backtest.Trades
            .Select(trade => (Trade: trade, Regime: regimes.TryGetValue(trade.EntryTimeUtc.Date, out var regime) ? regime : null))
            .Where(item => item.Regime != null)
            .GroupBy(item => (
                item.Regime!.Volatility,
                item.Regime.Trend,
                Hour: byHour ? item.Trade.EntryTimeUtc.Hour : -1));

        foreach (var group in groups.OrderBy(group => group.Key.Volatility)
                     .ThenBy(group => group.Key.Trend)
                     .ThenBy(group => group.Key.Hour))
        {
            var trades = group.Select(item => item.Trade).ToList();
            rows.Add(new RegimeResult
            {
                Symbol = backtest.Symbol,
                StrategyKey = backtest.StrategyKey,
                Volatility = group.Key.Volatility,
                Trend = group.Key.Trend,
                HourUtc = group.Key.Hour,
                TradeCount = trades.Count,
                NetPnL = trades.Sum(trade => trade.NetPnL),
                ExpectancyR = trades.Average(trade => trade.RMultiple),
                HitRatePercent = (decimal)trades.Count(trade => trade.NetPnL > 0m) / trades.Count * 100m,
            });
        }

        return rows;
    }

    /// <summary>Kumulierter Rückgang der R-Kurve - die kontogrößenunabhängige Drawdown-Größe des Erwartungsprofils.</summary>
    public static decimal MaxDrawdownR(IReadOnlyList<TradeRecord> trades)
    {
        if (trades == null || trades.Count == 0)
        {
            return 0m;
        }

        var cumulative = 0m;
        var peak = 0m;
        var worst = 0m;

        foreach (var trade in trades.OrderBy(trade => trade.ExitTimeUtc))
        {
            cumulative += trade.RMultiple;
            if (cumulative > peak)
            {
                peak = cumulative;
            }

            var drop = peak - cumulative;
            if (drop > worst)
            {
                worst = drop;
            }
        }

        return worst;
    }

    private static decimal Percentile(IReadOnlyList<decimal> sorted, decimal fraction)
    {
        if (sorted.Count == 0)
        {
            return 0m;
        }

        var index = (int)Math.Floor((double)(fraction * (sorted.Count - 1)));
        return sorted[Math.Max(0, Math.Min(sorted.Count - 1, index))];
    }

    public static string Describe(RegimeResult row) =>
        string.Format(
            CultureInfo.InvariantCulture,
            "{0} auf {1}: Vola {2}, Trend {3}{4} -> {5} Trades, netto {6:0.00}, E {7:0.###} R",
            row.StrategyKey, row.Symbol, row.Volatility, row.Trend,
            row.HourUtc >= 0 ? $", {row.HourUtc:00}:00 UTC" : string.Empty,
            row.TradeCount, row.NetPnL, row.ExpectancyR);
}
