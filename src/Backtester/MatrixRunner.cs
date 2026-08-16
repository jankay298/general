using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Daytrading.Data.Config;
using Daytrading.Execution;
using Daytrading.Strategies;
using Daytrading.Strategies.Model;

namespace Daytrading.Backtester;

/// <summary>Marktdaten eines Symbols für die Matrix.</summary>
public sealed class SymbolData
{
    public SymbolData(SymbolConfig config, IReadOnlyList<Candle> bars)
    {
        Config = config ?? throw new ArgumentNullException(nameof(config));
        Bars = bars ?? throw new ArgumentNullException(nameof(bars));
    }

    public SymbolConfig Config { get; }

    public IReadOnlyList<Candle> Bars { get; }
}

/// <summary>Gesamtergebnis eines Matrixlaufs.</summary>
public sealed class MatrixResult
{
    public IReadOnlyList<BacktestResult> Results { get; init; } = Array.Empty<BacktestResult>();

    /// <summary>
    /// Anzahl der insgesamt gerechneten Kombinationen. Steht im Bericht, damit einschätzbar
    /// bleibt, wie viel Zufall im besten Ergebnis stecken kann.
    /// </summary>
    public int CombinationsTested => Results.Count;

    public TimeSpan Duration { get; init; }
}

/// <summary>
/// Rechnet die Matrix aus allen Strategien, Symbolen und Parameterkombinationen.
/// </summary>
/// <remarks>
/// Genau das, wofür der eigene Backtester existiert: Der eingebaute Backtester von cTrader
/// rechnet immer nur ein Symbol und eine Strategie je Durchlauf.
///
/// Die Läufe sind voneinander unabhängig und laufen parallel; das Ergebnis wird anschließend
/// stabil sortiert, damit zwei Läufe mit identischem Input auch identische Dateien erzeugen.
/// </remarks>
public sealed class MatrixRunner
{
    private readonly RiskLimits _limits;
    private readonly BacktestOptions _options;

    public MatrixRunner(RiskLimits limits, BacktestOptions options)
    {
        _limits = limits ?? throw new ArgumentNullException(nameof(limits));
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public MatrixResult Run(
        IReadOnlyList<SymbolData> symbols,
        IReadOnlyList<StrategyRegistration> strategies,
        Action<BacktestResult>? onResult = null)
    {
        if (symbols == null)
        {
            throw new ArgumentNullException(nameof(symbols));
        }

        if (strategies == null)
        {
            throw new ArgumentNullException(nameof(strategies));
        }

        var started = DateTime.UtcNow;
        var jobs = new List<(SymbolData Symbol, StrategyRegistration Strategy, IReadOnlyDictionary<string, string> Parameters)>();

        foreach (var symbol in symbols)
        {
            foreach (var strategy in strategies)
            {
                var grid = strategy.ParameterGrid.Count > 0
                    ? strategy.ParameterGrid
                    : new IReadOnlyDictionary<string, string>[] { new Dictionary<string, string>() };

                foreach (var parameters in grid)
                {
                    jobs.Add((symbol, strategy, parameters));
                }
            }
        }

        var results = new ConcurrentBag<BacktestResult>();

        Parallel.ForEach(jobs, job =>
        {
            var parameters = new StrategyParameters(job.Parameters);
            var runner = new BacktestRunner();

            // Je Lauf eine frische Strategie-Instanz: Zustand darf nicht zwischen Läufen lecken.
            var result = runner.Run(new BacktestJob(
                job.Strategy.Factory(),
                parameters,
                job.Symbol.Config,
                job.Symbol.Bars,
                _limits,
                _options));

            results.Add(result);
            onResult?.Invoke(result);
        });

        return new MatrixResult
        {
            Results = results
                .OrderBy(result => result.Symbol, StringComparer.Ordinal)
                .ThenBy(result => result.StrategyKey, StringComparer.Ordinal)
                .ThenBy(result => result.ParameterFingerprint, StringComparer.Ordinal)
                .ToList(),
            Duration = DateTime.UtcNow - started,
        };
    }
}
