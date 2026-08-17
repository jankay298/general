using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Daytrading.Evaluation;
using Daytrading.Execution;

namespace Daytrading.Backtester;

/// <summary>
/// Schreibt Ergebnisse nach <c>/results</c>: die Matrix als CSV, jeden Trade einzeln,
/// die Regime-Tabelle, die Walk-Forward-Fenster, die Erwartungsprofile und eine lesbare
/// Zusammenfassung.
/// </summary>
/// <remarks>
/// Zahlen werden mit <see cref="CultureInfo.InvariantCulture"/> geschrieben, Trennzeichen ist
/// das Semikolon. Ein Ergebnisbericht, den die Ländereinstellung der Maschine verändert, wäre
/// nicht reproduzierbar.
/// </remarks>
public sealed class ResultWriter
{
    private readonly string _root;

    public ResultWriter(string rootDirectory)
    {
        _root = rootDirectory ?? throw new ArgumentNullException(nameof(rootDirectory));
        Directory.CreateDirectory(_root);
    }

    public string MatrixPath => Path.Combine(_root, "matrix.csv");

    public string SummaryPath => Path.Combine(_root, "summary.md");

    public string RegimePath => Path.Combine(_root, "regimes.csv");

    public string WalkForwardPath => Path.Combine(_root, "walkforward.csv");

    public void WriteMatrix(IReadOnlyList<BacktestResult> results)
    {
        var text = new StringBuilder();
        text.AppendLine(string.Join(";", new[]
        {
            "strategy", "symbol", "timeframe", "parameters", "from_utc", "to_utc",
            "trades", "net_pnl", "return_percent", "max_drawdown_amount", "max_drawdown_percent",
            "profit_factor", "sharpe", "sortino", "hit_rate_percent", "expectancy_per_trade",
            "expectancy_r", "avg_holding_minutes", "longest_losing_streak", "trades_per_week",
            "weeks_to_minimum_sample", "statistically_weak", "active_days", "stopped_at", "bars_processed", "rejections",
        }));

        foreach (var result in results)
        {
            var metrics = result.Metrics;
            text.AppendLine(string.Join(";", new[]
            {
                Escape(result.StrategyKey),
                Escape(result.Symbol),
                result.Timeframe.ToString(),
                Escape(result.ParameterFingerprint),
                Date(result.FromUtc),
                Date(result.ToUtc),
                Number(metrics.TradeCount),
                Number(metrics.NetPnL),
                Number(metrics.ReturnPercent),
                Number(metrics.MaxDrawdownAmount),
                Number(metrics.MaxDrawdownPercent),
                metrics.ProfitFactor.HasValue ? Number(metrics.ProfitFactor.Value) : string.Empty,
                Number(metrics.Sharpe),
                Number(metrics.Sortino),
                Number(metrics.HitRate),
                Number(metrics.ExpectancyPerTrade),
                Number(metrics.ExpectancyR),
                Number(metrics.AverageHoldingMinutes),
                Number(metrics.LongestLosingStreak),
                Number(metrics.TradesPerWeek),
                metrics.WeeksToMinimumSample.HasValue ? Number(metrics.WeeksToMinimumSample.Value) : string.Empty,
                metrics.IsStatisticallyWeak ? "ja" : "nein",
                Number(result.ActiveDays),
                result.StoppedAtUtc.HasValue ? Date(result.StoppedAtUtc.Value) : string.Empty,
                Number(result.BarsProcessed),
                Escape(string.Join(" ", result.Rejections.OrderByDescending(pair => pair.Value)
                    .Select(pair => $"{pair.Key}={pair.Value}"))),
            }));
        }

        File.WriteAllText(MatrixPath, text.ToString());
    }

    public string WriteTrades(BacktestResult result)
    {
        var directory = Path.Combine(_root, "trades");
        Directory.CreateDirectory(directory);

        var name = $"{Sanitize(result.StrategyKey)}_{Sanitize(result.Symbol)}_{ShortHash(result.ParameterFingerprint)}.csv";
        var path = Path.Combine(directory, name);

        var text = new StringBuilder();
        text.AppendLine(TradeRecordCsv.Header);
        foreach (var trade in result.Trades)
        {
            text.AppendLine(TradeRecordCsv.Format(trade));
        }

        File.WriteAllText(path, text.ToString());
        return path;
    }

    public void WriteRegimes(IReadOnlyList<RegimeResult> rows)
    {
        var text = new StringBuilder();
        text.AppendLine("strategy;symbol;volatility;trend;hour_utc;trades;net_pnl;expectancy_r;hit_rate_percent");

        foreach (var row in rows)
        {
            text.AppendLine(string.Join(";", new[]
            {
                Escape(row.StrategyKey),
                Escape(row.Symbol),
                row.Volatility.ToString(),
                row.Trend.ToString(),
                row.HourUtc >= 0 ? Number(row.HourUtc) : "alle",
                Number(row.TradeCount),
                Number(row.NetPnL),
                Number(row.ExpectancyR),
                Number(row.HitRatePercent),
            }));
        }

        File.WriteAllText(RegimePath, text.ToString());
    }

    public void WriteWalkForward(IReadOnlyList<WalkForwardReport> reports)
    {
        var text = new StringBuilder();
        text.AppendLine(
            "strategy;symbol;window;train_from;train_to;test_from;test_to;selected_parameters;" +
            "train_trades;train_expectancy_r;test_trades;test_net_pnl;test_expectancy_r;test_max_drawdown_percent;note");

        foreach (var report in reports)
        {
            foreach (var window in report.Windows)
            {
                text.AppendLine(string.Join(";", new[]
                {
                    Escape(report.StrategyKey),
                    Escape(report.Symbol),
                    Number(window.Index),
                    Date(window.TrainFromUtc),
                    Date(window.TrainToUtc),
                    Date(window.TestFromUtc),
                    Date(window.TestToUtc),
                    Escape(window.SelectedParameters),
                    Number(window.TrainResult?.Metrics.TradeCount ?? 0),
                    Number(window.TrainResult?.Metrics.ExpectancyR ?? 0m),
                    Number(window.TestResult?.Metrics.TradeCount ?? 0),
                    Number(window.TestResult?.Metrics.NetPnL ?? 0m),
                    Number(window.TestResult?.Metrics.ExpectancyR ?? 0m),
                    Number(window.TestResult?.Metrics.MaxDrawdownPercent ?? 0m),
                    Escape(window.Note ?? string.Empty),
                }));
            }
        }

        File.WriteAllText(WalkForwardPath, text.ToString());
    }

    public string WriteProfile(ExpectationProfile profile)
    {
        var directory = Path.Combine(_root, "profiles");
        Directory.CreateDirectory(directory);

        var path = Path.Combine(directory, $"{Sanitize(profile.StrategyKey)}_{Sanitize(profile.Symbol)}.json");
        var payload = new
        {
            profile.StrategyKey,
            profile.Symbol,
            profile.StrategyVersion,
            profile.ParameterFingerprint,
            profile.CreatedAtUtc,
            profile.WindowCount,
            profile.TotalTrades,
            ExpectancyR = Describe(profile.ExpectancyR),
            HitRatePercent = Describe(profile.HitRatePercent),
            TradesPerWeek = Describe(profile.TradesPerWeek),
            HoldingMinutes = Describe(profile.HoldingMinutes),
            profile.WorstDrawdownR,
            profile.LongestLosingStreak,
            profile.AssumedSpread,
            Hinweis = "Eingefroren beim Deployment. Ersetzen nur nach bewusster, dokumentierter Neuvalidierung.",
        };

        File.WriteAllText(path, JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }));
        return path;
    }

    public void WriteSummary(
        MatrixResult matrix,
        IReadOnlyList<WalkForwardReport> walkForward,
        BacktestOptions options,
        IReadOnlyList<string> notes)
    {
        var text = new StringBuilder();
        text.AppendLine("# Backtest-Zusammenfassung");
        text.AppendLine();
        text.AppendLine(Invariant($"Erstellt: {DateTime.UtcNow:yyyy-MM-dd HH:mm} UTC"));
        text.AppendLine(Invariant($"Startkapital: {options.StartingBalance}, Seed: {options.RandomSeed}"));
        text.AppendLine(Invariant($"Gerechnete Kombinationen: **{matrix.CombinationsTested}** in {matrix.Duration.TotalSeconds:0.0} s"));
        text.AppendLine();
        text.AppendLine("Je mehr Kombinationen gerechnet wurden, desto wahrscheinlicher ist es, dass die beste allein durch");
        text.AppendLine("Zufall entstanden ist. Diese Zahl gehört deshalb neben jedes Ergebnis.");
        text.AppendLine();

        text.AppendLine("## Kosten- und Ausführungsannahmen");
        text.AppendLine();
        text.AppendLine("- Ausführung von Einstiegen frühestens auf der Open der Folgebar");
        text.AppendLine("- Zeitgesteuerte Ausstiege auf dem Close der laufenden Bar (sonst wäre kein Flat vor Sessionende möglich)");
        text.AppendLine("- Spread je Symbol aus der Konfiguration, in Randzeiten mit dem konfigurierten Faktor multipliziert");
        text.AppendLine(Invariant($"- Slippage: {options.SlippageTicks} Ticks bei Marktorders, {options.StopSlippageTicks} Ticks bei Stops, keine bei Limits"));
        text.AppendLine("- Stop und Ziel in derselben Bar: der Stop gilt als zuerst erreicht");
        text.AppendLine("- Kurslücken über den Stop hinweg werden zum Eröffnungskurs gefüllt");
        text.AppendLine("- Swap wird bewusst ignoriert: Ohne Overnight-Positionen fällt keiner an");
        text.AppendLine();

        var ranked = matrix.Results
            .Where(result => result.Metrics.TradeCount > 0)
            .OrderByDescending(result => result.Metrics.ExpectancyR)
            .ToList();

        text.AppendLine("## Beste Kombinationen nach Erwartungswert je Trade");
        text.AppendLine();
        text.AppendLine("| Strategie | Symbol | Parameter | Trades | Netto | E (R) | MaxDD % | Belastbar |");
        text.AppendLine("|---|---|---|---|---|---|---|---|");

        foreach (var result in ranked.Take(15))
        {
            text.AppendLine(string.Format(
                CultureInfo.InvariantCulture,
                "| {0} | {1} | {2} | {3} | {4:0.00} | {5:0.###} | {6:0.00} | {7} |",
                result.StrategyKey, result.Symbol, result.ParameterFingerprint, result.Metrics.TradeCount,
                result.Metrics.NetPnL, result.Metrics.ExpectancyR, result.Metrics.MaxDrawdownPercent,
                result.Metrics.IsStatisticallyWeak ? "**nein**" : "ja"));
        }

        var weak = matrix.Results.Count(result => result.Metrics.IsStatisticallyWeak);
        text.AppendLine();
        text.AppendLine(string.Format(
            CultureInfo.InvariantCulture,
            "{0} von {1} Läufen haben zu wenige Trades für eine belastbare Aussage (Schwelle: {2}).",
            weak, matrix.Results.Count, options.MinimumSampleTrades));

        var stopped = matrix.Results.Where(result => result.StoppedAtUtc.HasValue).ToList();
        if (stopped.Count > 0)
        {
            text.AppendLine();
            text.AppendLine(string.Format(
                CultureInfo.InvariantCulture,
                "**{0} von {1} Läufen wurden vorzeitig beendet**, weil der Gesamt-Drawdown erreicht war. " +
                "Ihre Kennzahlen beziehen sich auf die Zeit bis dahin - im Median {2:0} Tage. Das ist keine " +
                "Schwäche des Backtests, sondern das Ergebnis: Diese Kombinationen hätten nach den Regeln " +
                "abgeschaltet werden müssen.",
                stopped.Count,
                matrix.Results.Count,
                stopped.Select(result => (decimal)result.ActiveDays).OrderBy(days => days).ElementAt(stopped.Count / 2)));
        }

        var slow = matrix.Results
            .Where(result => result.Metrics.WeeksToMinimumSample is > 52m)
            .OrderByDescending(result => result.Metrics.WeeksToMinimumSample)
            .ToList();

        if (slow.Count > 0)
        {
            text.AppendLine();
            text.AppendLine("## Kombinationen mit unrealistisch langer Anlaufzeit");
            text.AppendLine();
            text.AppendLine("Bei dieser Handelsfrequenz dauert es über ein Jahr, bis die Mindeststichprobe erreicht ist:");
            text.AppendLine();
            foreach (var result in slow.Take(10))
            {
                text.AppendLine(string.Format(
                    CultureInfo.InvariantCulture,
                    "- {0} / {1}: {2:0.##} Trades pro Woche, rund {3:0} Wochen bis zur Mindeststichprobe",
                    result.StrategyKey, result.Symbol, result.Metrics.TradesPerWeek, result.Metrics.WeeksToMinimumSample));
            }
        }

        if (walkForward.Count > 0)
        {
            text.AppendLine();
            text.AppendLine("## Walk-Forward");
            text.AppendLine();
            text.AppendLine("| Strategie | Symbol | Fenster | OOS-Trades | OOS E (R) | Profil belastbar |");
            text.AppendLine("|---|---|---|---|---|---|");

            foreach (var report in walkForward)
            {
                var oosTrades = report.Windows.Sum(window => window.TestResult?.Metrics.TradeCount ?? 0);
                var expectancy = report.Profile?.ExpectancyR.Mean ?? 0m;
                text.AppendLine(string.Format(
                    CultureInfo.InvariantCulture,
                    "| {0} | {1} | {2} | {3} | {4:0.###} | {5} |",
                    report.StrategyKey, report.Symbol, report.Windows.Count, oosTrades, expectancy,
                    report.Profile?.IsTrustworthy() == true ? "ja" : "**nein**"));
            }
        }

        if (notes.Count > 0)
        {
            text.AppendLine();
            text.AppendLine("## Hinweise");
            text.AppendLine();
            foreach (var note in notes)
            {
                text.AppendLine($"- {note}");
            }
        }

        text.AppendLine();
        text.AppendLine("## Was diese Zahlen nicht sagen");
        text.AppendLine();
        text.AppendLine("- Ein guter Backtest ist keine Vorhersage. Er zeigt, dass eine Regel in der Vergangenheit nicht falsch war.");
        text.AppendLine("- Vor dem Livebetrieb steht der Demobetrieb über mehrere Monate, gemessen am eingefrorenen Erwartungsprofil.");
        text.AppendLine("- Abweichungen bei Spread und Slippage sind ein Ausführungsproblem und kein Strategieproblem.");

        File.WriteAllText(SummaryPath, text.ToString());
    }

    private static object Describe(Statistic statistic) => new
    {
        statistic.Mean,
        statistic.StandardDeviation,
        statistic.Minimum,
        statistic.Maximum,
        statistic.SampleCount,
    };

    private static string Invariant(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);

    private static string Number(decimal value) => value.ToString("0.##########", CultureInfo.InvariantCulture);

    private static string Number(double value) => value.ToString("0.##########", CultureInfo.InvariantCulture);

    private static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);

    private static string Date(DateTime value) => value.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    private static string Escape(string value) =>
        string.IsNullOrEmpty(value) ? string.Empty : value.Replace(';', ',').Replace('\n', ' ').Replace('\r', ' ');

    private static string Sanitize(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var character in value)
        {
            builder.Append(char.IsLetterOrDigit(character) ? character : '-');
        }

        return builder.ToString();
    }

    private static string ShortHash(string value)
    {
        using var sha = SHA256.Create();
        var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(value ?? string.Empty));
        return BitConverter.ToString(hash, 0, 4).Replace("-", string.Empty).ToLowerInvariant();
    }
}
