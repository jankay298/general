using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Daytrading.Evaluation;

/// <summary>Ein Messwert über mehrere Walk-Forward-Fenster: Mittelwert, Streuung, Extremwerte.</summary>
public sealed class Statistic
{
    public Statistic(decimal mean, decimal standardDeviation, decimal minimum, decimal maximum, int sampleCount)
    {
        Mean = mean;
        StandardDeviation = standardDeviation;
        Minimum = minimum;
        Maximum = maximum;
        SampleCount = sampleCount;
    }

    public decimal Mean { get; }

    public decimal StandardDeviation { get; }

    public decimal Minimum { get; }

    public decimal Maximum { get; }

    public int SampleCount { get; }

    /// <summary>Unteres Band: Mittelwert minus <paramref name="k"/> Standardabweichungen.</summary>
    public decimal LowerBand(decimal k) => Mean - k * StandardDeviation;

    /// <summary>Oberes Band: Mittelwert plus <paramref name="k"/> Standardabweichungen.</summary>
    public decimal UpperBand(decimal k) => Mean + k * StandardDeviation;

    public static Statistic FromSamples(IEnumerable<decimal> values)
    {
        var list = values?.ToList() ?? new List<decimal>();
        if (list.Count == 0)
        {
            return new Statistic(0m, 0m, 0m, 0m, 0);
        }

        var mean = list.Average();
        var deviation = list.Count < 2
            ? 0m
            : (decimal)Math.Sqrt((double)(list.Sum(value => (value - mean) * (value - mean)) / (list.Count - 1)));

        return new Statistic(mean, deviation, list.Min(), list.Max(), list.Count);
    }

    public override string ToString() =>
        string.Format(
            CultureInfo.InvariantCulture,
            "{0:0.###} ± {1:0.###} (min {2:0.###}, max {3:0.###}, n={4})",
            Mean, StandardDeviation, Minimum, Maximum, SampleCount);
}

/// <summary>
/// Das eingefrorene Erwartungsprofil einer Kombination aus Strategie und Symbol.
/// </summary>
/// <remarks>
/// Entsteht aus der Walk-Forward-Analyse und ist danach <b>der Maßstab</b>, an dem der Demo-
/// und Livebetrieb gemessen wird. Es wird beim Deployment eingefroren und nur bei einer
/// bewussten, dokumentierten Neuvalidierung ersetzt.
///
/// Bewusst in R-Vielfachen statt in Kontowährung: Ein Profil, das an eine Kontogröße gebunden
/// wäre, ließe sich nach einer Ein- oder Auszahlung nicht mehr vergleichen.
///
/// Die <see cref="StrategyVersion"/> gehört dazu: Ändert sich die Strategie-Logik, gilt das
/// Profil nicht mehr - sonst wird Live-Verhalten gegen die Erwartung eines anderen Codes gemessen.
/// </remarks>
public sealed class ExpectationProfile
{
    public ExpectationProfile(
        string strategyKey,
        string symbol,
        DateTime createdAtUtc,
        int windowCount,
        int totalTrades,
        Statistic expectancyR,
        Statistic hitRatePercent,
        Statistic tradesPerWeek,
        Statistic holdingMinutes,
        decimal worstDrawdownR,
        int longestLosingStreak,
        decimal assumedSpread,
        string parameterFingerprint = "",
        string strategyVersion = "")
    {
        StrategyKey = strategyKey ?? throw new ArgumentNullException(nameof(strategyKey));
        Symbol = symbol ?? throw new ArgumentNullException(nameof(symbol));
        CreatedAtUtc = createdAtUtc;
        WindowCount = windowCount;
        TotalTrades = totalTrades;
        ExpectancyR = expectancyR ?? throw new ArgumentNullException(nameof(expectancyR));
        HitRatePercent = hitRatePercent ?? throw new ArgumentNullException(nameof(hitRatePercent));
        TradesPerWeek = tradesPerWeek ?? throw new ArgumentNullException(nameof(tradesPerWeek));
        HoldingMinutes = holdingMinutes ?? throw new ArgumentNullException(nameof(holdingMinutes));
        WorstDrawdownR = worstDrawdownR;
        LongestLosingStreak = longestLosingStreak;
        AssumedSpread = assumedSpread;
        ParameterFingerprint = parameterFingerprint ?? string.Empty;
        StrategyVersion = strategyVersion ?? string.Empty;
    }

    public string StrategyKey { get; }

    public string Symbol { get; }

    public string StrategyVersion { get; }

    public string ParameterFingerprint { get; }

    public DateTime CreatedAtUtc { get; }

    /// <summary>Anzahl der Walk-Forward-Fenster, aus denen das Profil stammt.</summary>
    public int WindowCount { get; }

    public int TotalTrades { get; }

    /// <summary>Erwartungswert je Trade in R.</summary>
    public Statistic ExpectancyR { get; }

    public Statistic HitRatePercent { get; }

    public Statistic TradesPerWeek { get; }

    public Statistic HoldingMinutes { get; }

    /// <summary>Schlimmster beobachteter Rückgang der kumulierten R-Kurve über alle Fenster.</summary>
    public decimal WorstDrawdownR { get; }

    /// <summary>Längste beobachtete Verlustserie über alle Fenster.</summary>
    public int LongestLosingStreak { get; }

    /// <summary>Spread-Annahme des Backtests. Weicht der reale Spread stark ab, ist das ein Ausführungsproblem.</summary>
    public decimal AssumedSpread { get; }

    /// <summary>Ein Profil aus zwei Fenstern und 15 Trades ist kein Maßstab.</summary>
    public bool IsTrustworthy(int minimumWindows = 3, int minimumTrades = 60) =>
        WindowCount >= minimumWindows && TotalTrades >= minimumTrades;

    public override string ToString() =>
        string.Format(
            CultureInfo.InvariantCulture,
            "{0} / {1}: E={2} R, Trefferquote={3} %, DD max {4:0.##} R, längste Verlustserie {5}, {6} Fenster",
            StrategyKey, Symbol, ExpectancyR, HitRatePercent, WorstDrawdownR, LongestLosingStreak, WindowCount);
}
