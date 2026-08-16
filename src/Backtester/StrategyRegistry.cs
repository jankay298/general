using System;
using System.Collections.Generic;
using System.Linq;
using Daytrading.Strategies;
using Daytrading.Strategies.Library;

namespace Daytrading.Backtester;

/// <summary>Eine Strategie samt dem Parameterraum, der im Backtest durchgerechnet wird.</summary>
public sealed class StrategyRegistration
{
    public StrategyRegistration(string name, Func<IStrategy> factory, IReadOnlyList<IReadOnlyDictionary<string, string>> parameterGrid)
    {
        Name = name;
        Factory = factory;
        ParameterGrid = parameterGrid;
    }

    public string Name { get; }

    public Func<IStrategy> Factory { get; }

    /// <summary>Alle zu testenden Parameterkombinationen. Leere Liste bedeutet: nur Defaults.</summary>
    public IReadOnlyList<IReadOnlyDictionary<string, string>> ParameterGrid { get; }
}

/// <summary>
/// Verzeichnis der verfügbaren Strategien.
/// </summary>
/// <remarks>
/// Die einzige Stelle, an der Strategien namentlich auftauchen. Eine neue Strategie wird hier
/// eingetragen und ist damit automatisch Teil der Matrix - ohne Änderung an Backtester,
/// Ausführungsschicht oder cBot.
///
/// Der Parameterraum wird bewusst klein gehalten. Jede zusätzliche Kombination erhöht die
/// Wahrscheinlichkeit, dass die beste allein durch Zufall entsteht; die Anzahl der getesteten
/// Kombinationen wird deshalb im Ergebnisbericht ausgewiesen.
/// </remarks>
public static class StrategyRegistry
{
    public static IReadOnlyList<StrategyRegistration> All { get; } = new[]
    {
        new StrategyRegistration(
            "OpeningRangeBreakout",
            () => new OpeningRangeBreakoutStrategy(),
            Grid(
                ("OpeningRangeMinutes", new[] { "15", "30", "60" }),
                ("TakeProfitR", new[] { "1.5", "2", "3" }),
                ("StopLossMode", new[] { "OppositeRangeSide", "AtrMultiple" }))),

        new StrategyRegistration(
            "VwapReversion",
            () => new VwapReversionStrategy(),
            Grid(
                ("BandSigma", new[] { "1.5", "2", "2.5" }),
                ("StopAtrMultiple", new[] { "1", "1.5", "2" }),
                ("MaxEntriesPerDay", new[] { "1", "2" }))),
    };

    public static StrategyRegistration Get(string name) =>
        All.FirstOrDefault(registration => string.Equals(registration.Name, name, StringComparison.OrdinalIgnoreCase))
        ?? throw new ArgumentException(
            $"Unbekannte Strategie '{name}'. Bekannt sind: {string.Join(", ", All.Select(item => item.Name))}.",
            nameof(name));

    /// <summary>Kreuzprodukt der angegebenen Parameterwerte, in stabiler Reihenfolge.</summary>
    public static IReadOnlyList<IReadOnlyDictionary<string, string>> Grid(
        params (string Key, string[] Values)[] dimensions)
    {
        var result = new List<IReadOnlyDictionary<string, string>> { new Dictionary<string, string>() };

        foreach (var dimension in dimensions)
        {
            var expanded = new List<IReadOnlyDictionary<string, string>>(result.Count * dimension.Values.Length);
            foreach (var combination in result)
            {
                foreach (var value in dimension.Values)
                {
                    var next = new Dictionary<string, string>(combination.ToDictionary(pair => pair.Key, pair => pair.Value))
                    {
                        [dimension.Key] = value,
                    };
                    expanded.Add(next);
                }
            }

            result = expanded;
        }

        return result;
    }
}
