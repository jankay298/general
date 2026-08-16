using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Daytrading.Evaluation;
using Daytrading.Execution;
using Daytrading.Strategies;
using Daytrading.Strategies.Model;

namespace Daytrading.Backtester;

/// <summary>Ein Fenster der Walk-Forward-Analyse.</summary>
public sealed class WalkForwardWindow
{
    public int Index { get; init; }

    public DateTime TrainFromUtc { get; init; }

    public DateTime TrainToUtc { get; init; }

    public DateTime TestFromUtc { get; init; }

    public DateTime TestToUtc { get; init; }

    /// <summary>Die im Trainingsfenster gewählte Parameterkombination.</summary>
    public string SelectedParameters { get; init; } = string.Empty;

    public BacktestResult? TrainResult { get; init; }

    /// <summary>Ergebnis auf unangetasteten Daten. Nur diese Zahl zählt.</summary>
    public BacktestResult? TestResult { get; init; }

    public string? Note { get; init; }
}

/// <summary>Ergebnis der Walk-Forward-Analyse einer Kombination.</summary>
public sealed class WalkForwardReport
{
    public string StrategyKey { get; init; } = string.Empty;

    public string Symbol { get; init; } = string.Empty;

    public IReadOnlyList<WalkForwardWindow> Windows { get; init; } = Array.Empty<WalkForwardWindow>();

    public ExpectationProfile? Profile { get; init; }

    /// <summary>Gesamtzahl gerechneter Kombinationen über alle Fenster - das Maß für die Zufallsgefahr.</summary>
    public int CombinationsTested { get; init; }

    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
}

/// <summary>
/// Rollierende Optimierung: auf einem Fenster Parameter wählen, im folgenden Fenster testen,
/// weiterrollen.
/// </summary>
/// <remarks>
/// Der Zweck ist nicht, gute Parameter zu finden, sondern zu messen, wie gut Parameter halten,
/// die man <b>ohne Kenntnis der Zukunft</b> gewählt hätte. Das Erwartungsprofil entsteht
/// ausschließlich aus den Out-of-Sample-Fenstern - Trainingsergebnisse fließen nie ein.
///
/// Die Auswahl im Trainingsfenster bevorzugt bewusst nicht den höchsten Gewinn, sondern
/// Erwartungswert bei ausreichender Stichprobe. Der beste Lauf aus vier Trades ist keiner.
/// </remarks>
public sealed class WalkForwardRunner
{
    private readonly RiskLimits _limits;
    private readonly BacktestOptions _options;
    private readonly int _minimumTradesForSelection;

    public WalkForwardRunner(RiskLimits limits, BacktestOptions options, int minimumTradesForSelection = 10)
    {
        _limits = limits ?? throw new ArgumentNullException(nameof(limits));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _minimumTradesForSelection = minimumTradesForSelection;
    }

    public WalkForwardReport Run(
        SymbolData symbol,
        StrategyRegistration strategy,
        int trainDays = 180,
        int testDays = 60)
    {
        if (symbol == null)
        {
            throw new ArgumentNullException(nameof(symbol));
        }

        if (strategy == null)
        {
            throw new ArgumentNullException(nameof(strategy));
        }

        if (trainDays < 1 || testDays < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(trainDays), "Fensterlängen müssen positiv sein.");
        }

        var warnings = new List<string>();
        var windows = new List<WalkForwardWindow>();

        if (symbol.Bars.Count == 0)
        {
            return new WalkForwardReport
            {
                StrategyKey = strategy.Name,
                Symbol = symbol.Config.Name,
                Warnings = new[] { "Keine Bars vorhanden." },
            };
        }

        var first = symbol.Bars[0].OpenTimeUtc.Date;
        var last = symbol.Bars[^1].OpenTimeUtc.Date.AddDays(1);
        var grid = strategy.ParameterGrid.Count > 0
            ? strategy.ParameterGrid
            : new IReadOnlyDictionary<string, string>[] { new Dictionary<string, string>() };

        var combinations = 0;
        var index = 0;
        var trainFrom = first;

        while (trainFrom.AddDays(trainDays + testDays) <= last)
        {
            var trainTo = trainFrom.AddDays(trainDays);
            var testTo = trainTo.AddDays(testDays);

            var trainBars = Slice(symbol.Bars, trainFrom, trainTo);
            var testBars = Slice(symbol.Bars, trainTo, testTo);

            BacktestResult? bestTrain = null;
            IReadOnlyDictionary<string, string>? bestParameters = null;
            var bestScore = decimal.MinValue;

            foreach (var parameters in grid)
            {
                combinations++;
                var result = RunOne(strategy, parameters, symbol, trainBars);
                var score = Score(result);

                if (score > bestScore)
                {
                    bestScore = score;
                    bestTrain = result;
                    bestParameters = parameters;
                }
            }

            string? note = null;
            if (bestTrain == null || bestTrain.Metrics.TradeCount < _minimumTradesForSelection)
            {
                note = string.Format(
                    CultureInfo.InvariantCulture,
                    "Trainingsfenster lieferte nur {0} Trades - die Parameterwahl ist nicht belastbar.",
                    bestTrain?.Metrics.TradeCount ?? 0);
                warnings.Add($"Fenster {index}: {note}");
            }

            combinations++;
            var test = RunOne(strategy, bestParameters ?? new Dictionary<string, string>(), symbol, testBars);

            windows.Add(new WalkForwardWindow
            {
                Index = index,
                TrainFromUtc = trainFrom,
                TrainToUtc = trainTo,
                TestFromUtc = trainTo,
                TestToUtc = testTo,
                SelectedParameters = new StrategyParameters(bestParameters).Fingerprint,
                TrainResult = bestTrain,
                TestResult = test,
                Note = note,
            });

            index++;
            trainFrom = trainFrom.AddDays(testDays);
        }

        if (windows.Count == 0)
        {
            warnings.Add(string.Format(
                CultureInfo.InvariantCulture,
                "Der Zeitraum {0:yyyy-MM-dd} bis {1:yyyy-MM-dd} reicht nicht für ein Fenster aus {2}+{3} Tagen.",
                first, last, trainDays, testDays));
        }

        return new WalkForwardReport
        {
            StrategyKey = strategy.Name,
            Symbol = symbol.Config.Name,
            Windows = windows,
            Profile = BuildProfile(strategy.Name, symbol, windows),
            CombinationsTested = combinations,
            Warnings = warnings,
        };
    }

    /// <summary>
    /// Baut das Erwartungsprofil aus den Out-of-Sample-Fenstern.
    /// </summary>
    public static ExpectationProfile? BuildProfile(string strategyKey, SymbolData symbol, IReadOnlyList<WalkForwardWindow> windows)
    {
        var tested = windows
            .Where(window => window.TestResult is { Metrics.TradeCount: > 0 })
            .Select(window => window.TestResult!)
            .ToList();

        if (tested.Count == 0)
        {
            return null;
        }

        return new ExpectationProfile(
            strategyKey,
            symbol.Config.Name,
            DateTime.UtcNow,
            tested.Count,
            tested.Sum(result => result.Metrics.TradeCount),
            Statistic.FromSamples(tested.Select(result => result.Metrics.ExpectancyR)),
            Statistic.FromSamples(tested.Select(result => result.Metrics.HitRate)),
            Statistic.FromSamples(tested.Select(result => result.Metrics.TradesPerWeek)),
            Statistic.FromSamples(tested.Select(result => (decimal)result.Metrics.AverageHoldingMinutes)),
            tested.Max(result => RegimeAnalysis.MaxDrawdownR(result.Trades)),
            tested.Max(result => result.Metrics.LongestLosingStreak),
            symbol.Config.TypicalSpread,
            windows.LastOrDefault()?.SelectedParameters ?? string.Empty);
    }

    /// <summary>
    /// Einfacher Train/Test-Schnitt: Parameter nur auf dem vorderen Teil wählen, einmalig auf
    /// dem hinteren testen. Der kleine Bruder der Walk-Forward-Analyse.
    /// </summary>
    public static (IReadOnlyList<Candle> Train, IReadOnlyList<Candle> Test) Split(
        IReadOnlyList<Candle> bars, decimal trainFraction = 0.7m)
    {
        if (bars == null)
        {
            throw new ArgumentNullException(nameof(bars));
        }

        if (trainFraction <= 0m || trainFraction >= 1m)
        {
            throw new ArgumentOutOfRangeException(nameof(trainFraction), trainFraction, "Anteil muss zwischen 0 und 1 liegen.");
        }

        var cut = (int)(bars.Count * trainFraction);
        return (bars.Take(cut).ToList(), bars.Skip(cut).ToList());
    }

    private BacktestResult RunOne(
        StrategyRegistration strategy,
        IReadOnlyDictionary<string, string> parameters,
        SymbolData symbol,
        IReadOnlyList<Candle> bars) =>
        new BacktestRunner().Run(new BacktestJob(
            strategy.Factory(),
            new StrategyParameters(parameters),
            symbol.Config,
            bars,
            _limits,
            _options));

    /// <summary>
    /// Auswahlkriterium im Trainingsfenster: Erwartungswert je Trade, gedämpft mit der Wurzel
    /// der Trade-Anzahl. Bevorzugt solide Ergebnisse mit ausreichend Stichprobe gegenüber
    /// Ausreißern aus wenigen Trades.
    /// </summary>
    private decimal Score(BacktestResult result)
    {
        if (result.Metrics.TradeCount == 0)
        {
            return decimal.MinValue;
        }

        var penalty = result.Metrics.TradeCount < _minimumTradesForSelection ? 0.25m : 1m;
        return result.Metrics.ExpectancyR * (decimal)Math.Sqrt(result.Metrics.TradeCount) * penalty;
    }

    private static IReadOnlyList<Candle> Slice(IReadOnlyList<Candle> bars, DateTime fromUtc, DateTime toUtc)
    {
        var result = new List<Candle>();
        foreach (var bar in bars)
        {
            if (bar.OpenTimeUtc >= fromUtc && bar.OpenTimeUtc < toUtc)
            {
                result.Add(bar);
            }
        }

        return result;
    }
}
