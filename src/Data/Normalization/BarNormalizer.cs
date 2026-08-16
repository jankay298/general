using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Daytrading.Strategies.Model;

namespace Daytrading.Data.Normalization;

/// <summary>Was beim Normalisieren einer Rohserie aufgefallen ist.</summary>
public sealed class NormalizationReport
{
    private readonly List<string> _conflicts = new List<string>();

    public int InputCount { get; internal set; }

    public int OutputCount { get; internal set; }

    /// <summary>Zeilen mit identischem Zeitstempel und identischen Werten - harmlos, wurden entfernt.</summary>
    public int IdenticalDuplicates { get; internal set; }

    /// <summary>
    /// Zeilen mit identischem Zeitstempel, aber unterschiedlichen Kursen. Das ist kein
    /// harmloses Duplikat, sondern ein Hinweis auf vermischte Quellen oder eine kaputte Datei.
    /// </summary>
    public int ConflictingDuplicates { get; internal set; }

    /// <summary>Zeilen, die nicht in chronologischer Reihenfolge geliefert wurden.</summary>
    public int OutOfOrderBars { get; internal set; }

    public IReadOnlyList<string> Conflicts => _conflicts;

    internal void AddConflict(string message)
    {
        // Bewusst begrenzt: Bei einer komplett kaputten Datei will niemand 200.000 Zeilen Log.
        if (_conflicts.Count < 20)
        {
            _conflicts.Add(message);
        }
    }

    public override string ToString() =>
        $"{InputCount} gelesen, {OutputCount} übernommen, {IdenticalDuplicates} Duplikate, " +
        $"{ConflictingDuplicates} widersprüchliche Duplikate, {OutOfOrderBars} unsortierte Bars";
}

/// <summary>
/// Bringt Rohdaten in die interne Form: chronologisch, duplikatfrei, UTC.
/// </summary>
/// <remarks>
/// Lücken werden hier <b>nicht</b> gefüllt. Interpolierte Bars sehen im Backtest aus wie
/// Marktdaten, sind aber erfunden - und erzeugen Trades, die es nie gegeben hätte.
/// Fehlende Bars sind Sache des Qualitätsreports.
/// </remarks>
public static class BarNormalizer
{
    public static IReadOnlyList<Candle> Normalize(IEnumerable<Candle> bars, out NormalizationReport report)
    {
        if (bars == null)
        {
            throw new ArgumentNullException(nameof(bars));
        }

        report = new NormalizationReport();
        var byTime = new Dictionary<DateTime, Candle>();
        var previousTime = DateTime.MinValue;
        var first = true;

        foreach (var bar in bars)
        {
            report.InputCount++;

            if (bar.OpenTimeUtc.Kind != DateTimeKind.Utc)
            {
                throw new MarketDataException(
                    $"Bar {bar.OpenTimeUtc:O} ist nicht UTC. Die Quelle muss vor der Normalisierung umrechnen.");
            }

            if (!first && bar.OpenTimeUtc < previousTime)
            {
                report.OutOfOrderBars++;
            }

            previousTime = bar.OpenTimeUtc;
            first = false;

            if (byTime.TryGetValue(bar.OpenTimeUtc, out var existing))
            {
                if (existing == bar)
                {
                    report.IdenticalDuplicates++;
                }
                else
                {
                    report.ConflictingDuplicates++;
                    report.AddConflict(string.Format(
                        CultureInfo.InvariantCulture,
                        "{0:yyyy-MM-dd HH:mm}: {1} vs. {2}",
                        bar.OpenTimeUtc, existing, bar));

                    // Der erste Wert gewinnt. Deterministisch und im Report vermerkt - raten
                    // wäre die schlechtere Alternative.
                }

                continue;
            }

            byTime.Add(bar.OpenTimeUtc, bar);
        }

        var result = byTime.Values.OrderBy(candle => candle.OpenTimeUtc).ToList();
        report.OutputCount = result.Count;
        return result;
    }
}
