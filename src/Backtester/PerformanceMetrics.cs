using System;
using System.Collections.Generic;
using System.Linq;
using Daytrading.Execution;

namespace Daytrading.Backtester;

/// <summary>Ein Punkt der Equity-Kurve.</summary>
public readonly struct EquityPoint
{
    public EquityPoint(DateTime timeUtc, decimal equity)
    {
        TimeUtc = timeUtc;
        Equity = equity;
    }

    public DateTime TimeUtc { get; }

    public decimal Equity { get; }
}

/// <summary>
/// Kennzahlen eines Backtest-Laufs.
/// </summary>
/// <remarks>
/// Alle Werte beziehen sich auf Ergebnisse <b>nach</b> Spread, Slippage und Kommission.
/// <see cref="IsStatisticallyWeak"/> markiert Läufe mit zu wenigen Trades - eine schöne
/// Equity-Kurve aus zwölf Trades ist kein Ergebnis, sondern eine Anekdote.
/// </remarks>
public sealed class PerformanceMetrics
{
    public int TradeCount { get; init; }

    public int Wins { get; init; }

    public int Losses { get; init; }

    public decimal GrossPnL { get; init; }

    public decimal Commission { get; init; }

    public decimal NetPnL { get; init; }

    public decimal ReturnPercent { get; init; }

    public decimal MaxDrawdownAmount { get; init; }

    public decimal MaxDrawdownPercent { get; init; }

    /// <summary>Summe der Gewinne geteilt durch Summe der Verluste. Ohne Verluste: null (nicht unendlich).</summary>
    public decimal? ProfitFactor { get; init; }

    public double Sharpe { get; init; }

    public double Sortino { get; init; }

    public decimal HitRate { get; init; }

    /// <summary>Erwartungswert je Trade in Kontowährung.</summary>
    public decimal ExpectancyPerTrade { get; init; }

    /// <summary>Erwartungswert je Trade in Vielfachen des geplanten Risikos - über Symbole hinweg vergleichbar.</summary>
    public decimal ExpectancyR { get; init; }

    public double AverageHoldingMinutes { get; init; }

    public int LongestLosingStreak { get; init; }

    public decimal TradesPerWeek { get; init; }

    /// <summary>
    /// Wie viele Kalenderwochen es bei dieser Handelsfrequenz bräuchte, bis die
    /// Mindeststichprobe der Bewertungsschicht erreicht ist. Null, wenn nie.
    /// </summary>
    public decimal? WeeksToMinimumSample { get; init; }

    public bool IsStatisticallyWeak { get; init; }

    public IReadOnlyDictionary<DayOfWeek, decimal> NetPnLByWeekday { get; init; } = new Dictionary<DayOfWeek, decimal>();

    public IReadOnlyDictionary<int, decimal> NetPnLByHourUtc { get; init; } = new Dictionary<int, decimal>();

    public IReadOnlyDictionary<ExitReason, int> ExitReasons { get; init; } = new Dictionary<ExitReason, int>();

    public static PerformanceMetrics Empty { get; } = new PerformanceMetrics();

    public static PerformanceMetrics Compute(
        IReadOnlyList<TradeRecord> trades,
        IReadOnlyList<EquityPoint> equityCurve,
        decimal startingBalance,
        int minimumSampleTrades = 30)
    {
        if (trades == null)
        {
            throw new ArgumentNullException(nameof(trades));
        }

        if (trades.Count == 0)
        {
            return new PerformanceMetrics { IsStatisticallyWeak = true };
        }

        var ordered = trades.OrderBy(trade => trade.ExitTimeUtc).ToList();
        var wins = ordered.Where(trade => trade.NetPnL > 0m).ToList();
        var losses = ordered.Where(trade => trade.NetPnL < 0m).ToList();

        var grossWin = wins.Sum(trade => trade.NetPnL);
        var grossLoss = Math.Abs(losses.Sum(trade => trade.NetPnL));
        var net = ordered.Sum(trade => trade.NetPnL);

        var (drawdownAmount, drawdownPercent) = MaxDrawdown(equityCurve, startingBalance);
        var span = ordered[^1].ExitTimeUtc - ordered[0].EntryTimeUtc;
        var weeks = Math.Max((decimal)span.TotalDays / 7m, 0.0001m);
        var tradesPerWeek = ordered.Count / weeks;

        return new PerformanceMetrics
        {
            TradeCount = ordered.Count,
            Wins = wins.Count,
            Losses = losses.Count,
            GrossPnL = ordered.Sum(trade => trade.GrossPnL),
            Commission = ordered.Sum(trade => trade.Commission),
            NetPnL = net,
            ReturnPercent = startingBalance <= 0m ? 0m : net / startingBalance * 100m,
            MaxDrawdownAmount = drawdownAmount,
            MaxDrawdownPercent = drawdownPercent,
            ProfitFactor = grossLoss > 0m ? grossWin / grossLoss : (decimal?)null,
            Sharpe = RiskAdjusted(equityCurve, downsideOnly: false),
            Sortino = RiskAdjusted(equityCurve, downsideOnly: true),
            HitRate = (decimal)wins.Count / ordered.Count * 100m,
            ExpectancyPerTrade = net / ordered.Count,
            ExpectancyR = ordered.Average(trade => trade.RMultiple),
            AverageHoldingMinutes = ordered.Average(trade => trade.HoldingTime.TotalMinutes),
            LongestLosingStreak = LongestStreak(ordered),
            TradesPerWeek = tradesPerWeek,
            WeeksToMinimumSample = tradesPerWeek > 0m ? minimumSampleTrades / tradesPerWeek : (decimal?)null,
            IsStatisticallyWeak = ordered.Count < minimumSampleTrades,
            NetPnLByWeekday = ordered
                .GroupBy(trade => trade.EntryTimeUtc.DayOfWeek)
                .ToDictionary(group => group.Key, group => group.Sum(trade => trade.NetPnL)),
            NetPnLByHourUtc = ordered
                .GroupBy(trade => trade.EntryTimeUtc.Hour)
                .ToDictionary(group => group.Key, group => group.Sum(trade => trade.NetPnL)),
            ExitReasons = ordered
                .GroupBy(trade => trade.ExitReason)
                .ToDictionary(group => group.Key, group => group.Count()),
        };
    }

    /// <summary>
    /// Größter Rückgang vom bis dahin höchsten Kontostand.
    /// </summary>
    /// <remarks>
    /// Das Hoch beginnt beim <b>Startkapital</b>, nicht beim ersten Punkt der Kurve. Der erste
    /// Punkt wird erst nach der ersten Bar geschrieben und kann bereits im Minus liegen; von ihm
    /// aus zu messen versteckt genau den Teil des Rückgangs, der vor ihm entstanden ist.
    ///
    /// Auf echten Golddaten meldete ein Lauf so 8.56 % Drawdown, obwohl er von der
    /// Risikoschicht bei 10 % abgeschaltet worden war - der Bericht widersprach sich selbst,
    /// und zwar in die angenehme Richtung.
    /// </remarks>
    private static (decimal Amount, decimal Percent) MaxDrawdown(IReadOnlyList<EquityPoint> curve, decimal startingBalance)
    {
        if (curve == null || curve.Count == 0)
        {
            return (0m, 0m);
        }

        var peak = Math.Max(startingBalance, curve[0].Equity);
        var worstAmount = 0m;
        var worstPercent = 0m;

        foreach (var point in curve)
        {
            if (point.Equity > peak)
            {
                peak = point.Equity;
            }

            var drop = peak - point.Equity;
            if (drop > worstAmount)
            {
                worstAmount = drop;
            }

            if (peak > 0m)
            {
                var percent = drop / peak * 100m;
                if (percent > worstPercent)
                {
                    worstPercent = percent;
                }
            }
        }

        return (worstAmount, worstPercent);
    }

    /// <summary>
    /// Sharpe beziehungsweise Sortino aus Tagesrenditen der Equity-Kurve.
    /// </summary>
    /// <remarks>
    /// Der Annualisierungsfaktor wird aus den Daten geschätzt statt pauschal auf 252 gesetzt:
    /// Krypto handelt an sieben Tagen die Woche, Aktien an rund 252 Tagen im Jahr. Ein fester
    /// Faktor würde die beiden unvergleichbar machen.
    /// </remarks>
    private static double RiskAdjusted(IReadOnlyList<EquityPoint> curve, bool downsideOnly)
    {
        if (curve == null || curve.Count < 3)
        {
            return 0d;
        }

        var daily = curve
            .GroupBy(point => point.TimeUtc.Date)
            .OrderBy(group => group.Key)
            .Select(group => (Date: group.Key, Equity: group.Last().Equity))
            .ToList();

        if (daily.Count < 3)
        {
            return 0d;
        }

        var returns = new List<double>(daily.Count - 1);
        for (var i = 1; i < daily.Count; i++)
        {
            if (daily[i - 1].Equity <= 0m)
            {
                continue;
            }

            returns.Add((double)(daily[i].Equity / daily[i - 1].Equity) - 1d);
        }

        if (returns.Count < 2)
        {
            return 0d;
        }

        var mean = returns.Average();
        var sample = downsideOnly ? returns.Where(value => value < 0d).ToList() : returns;
        if (sample.Count < 2)
        {
            return 0d;
        }

        var variance = sample.Sum(value => Math.Pow(value - (downsideOnly ? 0d : mean), 2)) / (sample.Count - 1);
        var deviation = Math.Sqrt(variance);
        if (deviation <= 0d)
        {
            return 0d;
        }

        var years = Math.Max((daily[^1].Date - daily[0].Date).TotalDays / 365.25d, 1d / 365.25d);
        var periodsPerYear = Math.Max(daily.Count / years, 1d);
        return mean / deviation * Math.Sqrt(periodsPerYear);
    }

    private static int LongestStreak(IReadOnlyList<TradeRecord> trades)
    {
        var longest = 0;
        var current = 0;

        foreach (var trade in trades)
        {
            if (trade.NetPnL < 0m)
            {
                current++;
                longest = Math.Max(longest, current);
            }
            else
            {
                current = 0;
            }
        }

        return longest;
    }
}
