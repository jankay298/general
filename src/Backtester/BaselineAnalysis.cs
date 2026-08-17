using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Daytrading.Execution;

namespace Daytrading.Backtester;

/// <summary>
/// Zerlegt Trades nach Merkmalen und hält jede Gruppe gegen den Münzwurf.
/// </summary>
/// <remarks>
/// Eine Auswertung ohne Vergleichsgröße erzeugt zuverlässig Scheinfunde. Wer 24 Stunden,
/// 7 Wochentage und 5 Haltedauern durchsieht, findet bei reinem Zufall mehrere Gruppen, die
/// "deutlich" besser sind - das ist keine Entdeckung, das ist die Anzahl der Vergleiche.
///
/// Deshalb zwei Vorkehrungen. Erstens ein Rauschband: Wie weit ein Mittelwert aus n Trades
/// allein durch Zufall abweicht, hängt an n; ohne diese Spanne ist keine Zahl zu deuten.
/// Zweitens dieselbe Zerlegung für <c>RandomEntry</c>. Zeigt der Münzwurf dasselbe Muster,
/// ist das Muster keine Eigenschaft der Strategie.
///
/// Das ist nicht theoretisch: Nach Haltedauer sortiert ergaben echte Strategien +0.25 R über
/// vier Stunden und -0.54 R unter fünfzehn Minuten - und der Münzwurf ebenso. Ein Trade, der
/// schnell in den Stop läuft, hat kurz gehalten <i>und</i> verloren. Dasselbe Ereignis, zweimal
/// gemessen. Beim Einstieg ist die Haltedauer unbekannt und taugt deshalb zu keiner Regel.
/// </remarks>
public static class BaselineAnalysis
{
    /// <summary>Der Name, unter dem die Münzwurf-Strategie im Katalog steht.</summary>
    public const string BaselineStrategy = "RandomEntry";

    private sealed class Group
    {
        private readonly List<decimal> _values = new List<decimal>();

        public int Count => _values.Count;

        public decimal Mean => _values.Count == 0 ? 0m : _values.Sum() / _values.Count;

        public void Add(decimal value) => _values.Add(value);

        /// <summary>Zwei Standardfehler - die Spanne, in der ein Mittelwert zufällig schwankt.</summary>
        public decimal NoiseBand()
        {
            if (_values.Count < 2)
            {
                return decimal.MaxValue;
            }

            var mean = Mean;
            var variance = _values.Sum(value => (value - mean) * (value - mean)) / _values.Count;
            return 2m * (decimal)Math.Sqrt((double)variance) / (decimal)Math.Sqrt(_values.Count);
        }
    }

    /// <summary>Ein Merkmal, nach dem zerlegt wird - Name und die Einordnung eines Trades.</summary>
    public sealed class Dimension
    {
        public Dimension(string name, Func<TradeRecord, string> classify)
        {
            Name = name;
            Classify = classify;
        }

        public string Name { get; }

        public Func<TradeRecord, string> Classify { get; }
    }

    /// <summary>
    /// Merkmale, die <b>beim Einstieg bekannt</b> sind.
    /// </summary>
    /// <remarks>
    /// Ausstiegsgrund und Haltedauer fehlen hier mit Absicht. Nach ihnen zu sortieren ist immer
    /// aufschlussreich und nie verwertbar: Beide stehen erst fest, wenn der Trade vorbei ist.
    /// Eine Regel kann sich nur auf das stützen, was zum Zeitpunkt der Entscheidung vorliegt.
    /// </remarks>
    public static IReadOnlyList<Dimension> EntryTimeDimensions { get; } = new[]
    {
        new Dimension("Einstiegsstunde (UTC)", trade => trade.EntryTimeUtc.Hour.ToString("00 'Uhr'", CultureInfo.InvariantCulture)),
        new Dimension("Wochentag", trade => trade.EntryTimeUtc.DayOfWeek.ToString()),
        new Dimension("Richtung", trade => trade.Direction.ToString()),
        new Dimension("Symbol", trade => trade.Symbol),
        new Dimension("Stopweite in % des Kurses", trade => StopWidthBucket(trade)),
    };

    private static string StopWidthBucket(TradeRecord trade)
    {
        if (trade.EntryPrice <= 0m)
        {
            return "unbekannt";
        }

        var percent = Math.Abs(trade.EntryPrice - trade.StopLoss) / trade.EntryPrice * 100m;
        if (percent < 0.1m) return "< 0.1 %";
        if (percent < 0.2m) return "0.1 - 0.2 %";
        if (percent < 0.4m) return "0.2 - 0.4 %";
        if (percent < 0.8m) return "0.4 - 0.8 %";
        return "> 0.8 %";
    }

    /// <summary>
    /// Schreibt den Bericht: je Merkmal eine Tabelle, Strategie gegen Münzwurf.
    /// </summary>
    /// <param name="minimumTrades">
    /// Gruppen darunter werden weggelassen. Bei kleinen Gruppen ist das Rauschband größer als
    /// jeder denkbare Effekt - sie aufzuführen lädt nur dazu ein, Zufall zu deuten.
    /// </param>
    public static string Report(
        IReadOnlyList<TradeRecord> trades,
        int minimumTrades = 150,
        IReadOnlyList<Dimension>? dimensions = null)
    {
        if (trades == null)
        {
            throw new ArgumentNullException(nameof(trades));
        }

        var baseline = trades.Where(IsBaseline).ToList();
        var strategies = trades.Where(trade => !IsBaseline(trade)).ToList();

        var text = new StringBuilder();
        text.AppendLine("# Auswertung gegen den Münzwurf");
        text.AppendLine();
        text.AppendLine(Line("Strategien", strategies));
        text.AppendLine(Line("Münzwurf", baseline));
        text.AppendLine();
        text.AppendLine(
            "Der Münzwurf steigt zufällig ein, sonst identisch: gleicher Stop, gleiches Ziel, gleiche Kosten. " +
            "Er ist der Maßstab. Eine Gruppe zählt erst, wenn sie ihn um mehr als das Rauschband schlägt - " +
            "und wenn er dieselbe Auffälligkeit nicht selbst zeigt.");
        text.AppendLine();

        foreach (var dimension in dimensions ?? EntryTimeDimensions)
        {
            text.AppendLine($"## {dimension.Name}");
            text.AppendLine();
            text.AppendLine("| Gruppe | Trades | E(R) | Rauschband | Münzwurf | Differenz | Urteil |");
            text.AppendLine("|---|---:|---:|---:|---:|---:|---|");

            var byGroup = Aggregate(strategies, dimension);
            var baseGroups = Aggregate(baseline, dimension);

            foreach (var pair in byGroup.OrderByDescending(item => item.Value.Mean))
            {
                if (pair.Value.Count < minimumTrades)
                {
                    continue;
                }

                var band = pair.Value.NoiseBand();
                var hasBase = baseGroups.TryGetValue(pair.Key, out var reference) && reference.Count >= 30;
                var difference = hasBase ? pair.Value.Mean - reference!.Mean : 0m;
                var combined = hasBase
                    ? (decimal)Math.Sqrt(Math.Pow((double)band, 2) + Math.Pow((double)reference!.NoiseBand(), 2))
                    : 0m;

                var verdict = !hasBase
                    ? "kein Vergleich"
                    : difference > combined ? "**besser als Zufall**"
                    : difference < -combined ? "schlechter als Zufall"
                    : "wie Zufall";

                text.AppendLine(string.Format(
                    CultureInfo.InvariantCulture,
                    "| {0} | {1:N0} | {2:+0.000;-0.000} | ±{3:0.000} | {4} | {5} | {6} |",
                    pair.Key,
                    pair.Value.Count,
                    pair.Value.Mean,
                    band,
                    hasBase ? reference!.Mean.ToString("+0.000;-0.000", CultureInfo.InvariantCulture) : "-",
                    hasBase ? difference.ToString("+0.000;-0.000", CultureInfo.InvariantCulture) : "-",
                    verdict));
            }

            text.AppendLine();
        }

        return text.ToString();
    }

    private static bool IsBaseline(TradeRecord trade) =>
        trade.StrategyKey.StartsWith(BaselineStrategy, StringComparison.OrdinalIgnoreCase);

    private static Dictionary<string, Group> Aggregate(IEnumerable<TradeRecord> trades, Dimension dimension)
    {
        var result = new Dictionary<string, Group>(StringComparer.Ordinal);
        foreach (var trade in trades)
        {
            var key = dimension.Classify(trade);
            if (!result.TryGetValue(key, out var group))
            {
                group = new Group();
                result[key] = group;
            }

            group.Add(trade.RMultiple);
        }

        return result;
    }

    private static string Line(string label, IReadOnlyList<TradeRecord> trades) =>
        trades.Count == 0
            ? $"- **{label}**: keine Trades"
            : string.Format(
                CultureInfo.InvariantCulture,
                "- **{0}**: {1:N0} Trades, E(R) {2:+0.000;-0.000}",
                label, trades.Count, trades.Sum(trade => trade.RMultiple) / trades.Count);
}
