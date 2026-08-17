using System;
using System.Collections.Generic;
using System.Linq;
using Daytrading.Strategies.Library;

namespace Daytrading.Strategies;

/// <summary>
/// Verzeichnis aller Strategien, ansprechbar über ihren Namen.
/// </summary>
/// <remarks>
/// Liegt bewusst in der Strategie-Bibliothek und nicht im Backtester: Backtester und cBot müssen
/// unter demselben Namen dieselbe Strategie erzeugen. Zwei getrennte Verzeichnisse wären der
/// sicherste Weg, im Livebetrieb etwas anderes laufen zu lassen als im Test.
///
/// Eine neue Strategie wird hier eingetragen - danach kennt der Backtester sie automatisch,
/// und im cBot lässt sie sich über den Strategie-Parameter auswählen.
/// </remarks>
public static class StrategyCatalog
{
    private static readonly Dictionary<string, Func<IStrategy>> Factories =
        new Dictionary<string, Func<IStrategy>>(StringComparer.OrdinalIgnoreCase)
        {
            ["OpeningRangeBreakout"] = () => new OpeningRangeBreakoutStrategy(),
            ["VwapReversion"] = () => new VwapReversionStrategy(),
            ["DonchianBreakout"] = () => new DonchianBreakoutStrategy(),
            ["TrendPullback"] = () => new TrendPullbackStrategy(),
            ["RsiReversal"] = () => new RsiReversalStrategy(),
            ["VolatilitySqueeze"] = () => new VolatilitySqueezeStrategy(),
            ["ImpulsePullback"] = () => new ImpulsePullbackStrategy(),
            ["LiquiditySweep"] = () => new LiquiditySweepStrategy(),

            // Keine Strategie, sondern der Vergleichsmassstab: Einstieg per Muenzwurf.
            ["RandomEntry"] = () => new RandomEntryStrategy(),

            // Die gespiegelten Fassungen. Sie beantworten die Frage, ob ein Verlust aus der
            // Richtung kommt oder aus den Kosten - siehe InvertedStrategy.
            ["InvertedOpeningRangeBreakout"] = () => new InvertedStrategy(new OpeningRangeBreakoutStrategy()),
            ["InvertedVwapReversion"] = () => new InvertedStrategy(new VwapReversionStrategy()),
            ["InvertedDonchianBreakout"] = () => new InvertedStrategy(new DonchianBreakoutStrategy()),
            ["InvertedTrendPullback"] = () => new InvertedStrategy(new TrendPullbackStrategy()),
            ["InvertedRsiReversal"] = () => new InvertedStrategy(new RsiReversalStrategy()),
            ["InvertedVolatilitySqueeze"] = () => new InvertedStrategy(new VolatilitySqueezeStrategy()),
        };

    public static IReadOnlyList<string> Names => Factories.Keys.OrderBy(name => name, StringComparer.Ordinal).ToList();

    public static IStrategy Create(string name)
    {
        if (!TryCreate(name, out var strategy))
        {
            throw new ArgumentException(
                $"Unbekannte Strategie '{name}'. Bekannt sind: {string.Join(", ", Names)}.", nameof(name));
        }

        return strategy!;
    }

    public static bool TryCreate(string name, out IStrategy? strategy)
    {
        if (!string.IsNullOrWhiteSpace(name) && Factories.TryGetValue(name.Trim(), out var factory))
        {
            strategy = factory();
            return true;
        }

        strategy = null;
        return false;
    }

    /// <summary>
    /// Liest Parameter aus einer Zeile der Form <c>Key=Value;Key=Value</c> - so lassen sich
    /// Strategieparameter über ein einziges cTrader-Eingabefeld setzen.
    /// </summary>
    public static StrategyParameters ParseParameters(string? text)
    {
        var values = new List<KeyValuePair<string, string>>();
        if (string.IsNullOrWhiteSpace(text))
        {
            return new StrategyParameters(values);
        }

        foreach (var part in text!.Split(new[] { ';', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = part.IndexOf('=');
            if (separator <= 0)
            {
                throw new StrategyParameterException(
                    $"'{part.Trim()}' ist kein Parameter. Erwartet wird Name=Wert, mehrere getrennt durch Semikolon.");
            }

            values.Add(new KeyValuePair<string, string>(
                part.Substring(0, separator).Trim(),
                part.Substring(separator + 1).Trim()));
        }

        return new StrategyParameters(values);
    }
}
