using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Daytrading.Backtester;
using Daytrading.Data;
using Daytrading.Data.Config;
using Daytrading.Data.Providers;
using Daytrading.Data.Quality;
using Daytrading.Data.Storage;
using Daytrading.Execution;
using Daytrading.Strategies.Model;

var arguments = CommandLine.Parse(args);
var command = arguments.Command;

try
{
    switch (command)
    {
        case "ingest":
            await Commands.IngestAsync(arguments);
            break;
        case "backtest":
            await Commands.BacktestAsync(arguments);
            break;
        case "walkforward":
            await Commands.WalkForwardAsync(arguments);
            break;
        case "demo":
            Commands.Demo(arguments);
            break;
        default:
            CommandLine.PrintUsage();
            return command is "help" or "--help" or "-h" ? 0 : 1;
    }

    return 0;
}
catch (Exception error)
{
    Console.Error.WriteLine();
    Console.Error.WriteLine($"Abbruch: {error.Message}");
    if (arguments.Has("verbose"))
    {
        Console.Error.WriteLine(error);
    }

    return 1;
}

/// <summary>Sehr einfache Kommandozeile: ein Befehl, danach --schlüssel wert.</summary>
internal sealed class CommandLine
{
    private readonly Dictionary<string, string> _values = new(StringComparer.OrdinalIgnoreCase);

    public string Command { get; private init; } = "help";

    public static CommandLine Parse(string[] args)
    {
        var line = new CommandLine { Command = args.Length > 0 ? args[0].ToLowerInvariant() : "help" };

        for (var i = 1; i < args.Length; i++)
        {
            if (!args[i].StartsWith("--", StringComparison.Ordinal))
            {
                continue;
            }

            var key = args[i][2..];
            var value = i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal)
                ? args[++i]
                : "true";

            line._values[key] = value;
        }

        return line;
    }

    public bool Has(string key) => _values.ContainsKey(key);

    public string Get(string key, string fallback) => _values.TryGetValue(key, out var value) ? value : fallback;

    public int GetInt(string key, int fallback) =>
        _values.TryGetValue(key, out var value) && int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : fallback;

    public decimal GetDecimal(string key, decimal fallback) =>
        _values.TryGetValue(key, out var value) && decimal.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : fallback;

    public DateTime GetDate(string key, DateTime fallback)
    {
        if (!_values.TryGetValue(key, out var value))
        {
            return fallback;
        }

        if (!DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var parsed))
        {
            throw new ArgumentException($"'{value}' ist kein Datum im Format yyyy-MM-dd.");
        }

        return DateTime.SpecifyKind(parsed.Date, DateTimeKind.Utc);
    }

    public static void PrintUsage()
    {
        Console.WriteLine("""
        Daytrading-Backtester

        Befehle:
          demo          Kompletter Durchlauf auf synthetischen Daten. Braucht weder Netz noch Broker.
          ingest        Marktdaten laden, pruefen und als Parquet ablegen.
          backtest      Matrix aus Strategien x Symbolen x Parametern rechnen.
          walkforward   Rollierende Optimierung und Erwartungsprofile erzeugen.

        Gemeinsame Optionen:
          --config <pfad>     Symbolkonfiguration      (Vorgabe: config/symbols.json)
          --risk <pfad>       Risikogrenzen            (Vorgabe: config/risk.defaults.json)
          --data <pfad>       Datenverzeichnis         (Vorgabe: data)
          --results <pfad>    Ergebnisverzeichnis      (Vorgabe: results)
          --from / --to       Zeitraum, yyyy-MM-dd
          --symbol <name>     Nur dieses Symbol
          --strategy <name>   Nur diese Strategie
          --balance <zahl>    Startkapital             (Vorgabe: 10000)
          --spread-factor <f> Spread-Annahmen skalieren, z.B. 0.1 oder 3 (Vorgabe: 1)
          --max-cost-share <p> Einstiege ablehnen, wenn Kosten mehr als p % des Risikos sind (Vorgabe: 100 = aus)
          --breakeven-r <r>   Stop auf Einstieg ab r R Vorsprung (Vorgabe: 0 = aus)
          --trail-r <r>       Stop r R hinter dem Bestkurs nachziehen (Vorgabe: 0 = aus)
          --trail-start-r <r> Trailing beginnt ab r R Vorsprung (Vorgabe: 1)
          --limit-entries     Einstiege als Limit auf dem Signalkurs statt als Marktorder
          --verbose           Vollstaendige Fehlerausgabe

        Nur fuer ingest:
          --source binance|dukascopy|csv|oanda   Datenquelle (Vorgabe: binance)
          --csv <pfad>                 Verzeichnis mit CSV-Dateien (fuer source=csv)
          --throttle <ms>              Pause zwischen Downloads (Vorgabe: 250, nur dukascopy)

        Nur fuer walkforward:
          --train <tage>      Laenge des Trainingsfensters (Vorgabe: 180)
          --test <tage>       Laenge des Testfensters      (Vorgabe: 60)

        Beispiele:
          dotnet run --project src/Backtester -- demo
          dotnet run --project src/Backtester -- ingest --symbol BTCUSD --from 2023-01-01 --to 2024-01-01
          dotnet run --project src/Backtester -- backtest --from 2023-01-01 --to 2024-01-01
        """);
    }
}

internal static class Commands
{
    public static async Task IngestAsync(CommandLine arguments)
    {
        var catalog = SymbolCatalog.Load(arguments.Get("config", "config/symbols.json"));
        var dataRoot = arguments.Get("data", "data");
        var from = arguments.GetDate("from", DateTime.UtcNow.Date.AddYears(-1));
        var to = arguments.GetDate("to", DateTime.UtcNow.Date);

        var store = new ParquetBarStore(Path.Combine(dataRoot, "normalized"));
        var pipeline = new DataPipeline(
            store,
            Path.Combine(dataRoot, "manifests"),
            Path.Combine(arguments.Get("results", "results"), "data-quality"));

        foreach (var symbol in Selected(catalog, arguments))
        {
            using var downloader = new HttpFileDownloader();
            IDataProvider provider = arguments.Get("source", "binance").ToLowerInvariant() switch
            {
                "binance" => new BinancePublicDataProvider(downloader, Path.Combine(dataRoot, "raw", "binance")),
                "csv" => new CsvFileDataProvider(arguments.Get("csv", Path.Combine(dataRoot, "raw", "csv")), symbol.ToTimeframe()),
                "oanda" => new OandaDataProvider(),
                "dukascopy" => new DukascopyDataProvider(
                    downloader,
                    Path.Combine(dataRoot, "raw", "dukascopy", symbol.SourceNameFor("dukascopy")),
                    DukascopyDataProvider.PriceScaleFor(symbol.SourceNameFor("dukascopy")),
                    arguments.GetInt("throttle", 250)),
                var other => throw new ArgumentException($"Unbekannte Quelle '{other}'."),
            };

            if (provider is DukascopyDataProvider progressing)
            {
                // Diese Quelle braucht fuer mehrere Jahre Stunden. Eine Zeile je angefangenem
                // Monat reicht, um zu sehen, dass es vorangeht - ohne das Log zuzumuellen.
                var lastMonthReported = -1;
                var started = DateTime.UtcNow;

                progressing.Progress = (done, total, day) =>
                {
                    if (day.Month == lastMonthReported || total == 0)
                    {
                        return;
                    }

                    lastMonthReported = day.Month;
                    var share = (double)done / total;
                    var estimate = share > 0.01
                        ? $", noch etwa {TimeSpan.FromSeconds((DateTime.UtcNow - started).TotalSeconds * (1 - share) / share):hh\\:mm}"
                        : string.Empty;

                    Console.WriteLine($"  {day:yyyy-MM} ... ({done}/{total} Tage{estimate})");
                };
            }

            Console.WriteLine($"Lade {symbol.Name} von {provider.Name} ({from:yyyy-MM-dd} bis {to:yyyy-MM-dd}) ...");
            var result = await pipeline.IngestAsync(symbol, provider, from, to);

            Console.WriteLine(
                $"  {result.Report.BarCount} Bars, Status {result.Report.Status}, " +
                $"{result.Report.MissingBars} fehlend ({result.Report.MissingPercent:0.00} %)");
            Console.WriteLine($"  Bericht: {result.ReportPath}");

            if (provider is DukascopyDataProvider dukascopy)
            {
                // Der gemessene Spread ist der eigentliche Grund für diese Quelle - er gehört
                // sichtbar in die Ausgabe, damit er mit dem Wert in symbols.json vergleichbar ist.
                if (dukascopy.LastSpreadStatistics is { } spread)
                {
                    Console.WriteLine(
                        $"  Gemessener Spread aus {spread.SampleCount} Bars: " +
                        $"Median {spread.Median:0.#####}, Mittel {spread.Average:0.#####}, " +
                        $"Maximum {spread.Maximum:0.#####} " +
                        $"(Annahme in symbols.json: {symbol.TypicalSpread:0.#####})");
                }

                Console.WriteLine(
                    $"  {dukascopy.PaddingMinutesDropped} Auffuell-Minuten ohne Volumen verworfen, " +
                    $"{dukascopy.DaysWithoutData.Count} Tage ohne Daten.");

                if (dukascopy.DaysFailed.Count > 0)
                {
                    // Diese Tage fehlen wegen einer Stoerung, nicht weil der Markt zu war. Der
                    // Unterschied entscheidet, ob das Ergebnis belastbar ist.
                    Console.WriteLine(
                        $"  ACHTUNG: {dukascopy.DaysFailed.Count} Tage konnten nicht geladen werden " +
                        "(Netz oder Drosselung), sie fehlen in den Daten.");

                    foreach (var failure in dukascopy.DaysFailed.Take(5))
                    {
                        Console.WriteLine($"    {failure.DayUtc:yyyy-MM-dd}: {failure.Reason}");
                    }

                    if (dukascopy.DaysFailed.Count > 5)
                    {
                        Console.WriteLine($"    ... und {dukascopy.DaysFailed.Count - 5} weitere.");
                    }

                    Console.WriteLine(
                        "    Denselben Befehl noch einmal ausfuehren - Geladenes liegt im Cache, " +
                        "nur die Luecken werden nachgeholt.");
                }
            }

            if (!result.IsUsable)
            {
                Console.WriteLine("  ACHTUNG: Dieses Symbol fliegt aus dem Backtest. Gruende stehen im Bericht.");
            }

            (provider as IDisposable)?.Dispose();
        }
    }

    public static async Task BacktestAsync(CommandLine arguments)
    {
        var (symbols, notes) = await LoadAsync(arguments);
        Run(arguments, symbols, notes, walkForward: false);
    }

    public static async Task WalkForwardAsync(CommandLine arguments)
    {
        var (symbols, notes) = await LoadAsync(arguments);
        Run(arguments, symbols, notes, walkForward: true);
    }

    public static void Demo(CommandLine arguments)
    {
        var from = arguments.GetDate("from", new DateTime(2023, 1, 2, 0, 0, 0, DateTimeKind.Utc));
        var to = arguments.GetDate("to", new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var catalog = SymbolCatalog.Load(arguments.Get("config", "config/symbols.json"));

        Console.WriteLine("Demo-Lauf auf synthetischen Daten. Diese Ergebnisse sagen nichts ueber Strategien aus,");
        Console.WriteLine("sondern nur, dass Pipeline, Risikoschicht, Kostenmodell und Auswertung zusammenspielen.");
        Console.WriteLine();

        var symbols = new List<SymbolData>();
        var seed = arguments.GetInt("seed", 20240301);

        foreach (var config in Selected(catalog, arguments))
        {
            // Bewusst kein string.GetHashCode(): der ist in .NET je Prozess zufällig gesalzen,
            // zwei Läufe würden unterschiedliche Kursdaten erzeugen.
            var bars = SyntheticMarket.Generate(config, from, to, seed + StableHash(config.Name));
            Console.WriteLine($"{config.Name}: {bars.Count} synthetische Bars ({config.WorkingTimeframe})");
            symbols.Add(new SymbolData(config, bars));
        }

        Run(arguments, symbols, new List<string> { "Synthetische Daten - keine Aussage ueber reale Strategien." }, walkForward: true);
    }

    private static void Run(CommandLine arguments, IReadOnlyList<SymbolData> symbols, List<string> notes, bool walkForward)
    {
        if (symbols.Count == 0)
        {
            Console.WriteLine("Keine Symbole mit brauchbaren Daten. Abbruch.");
            return;
        }

        var limits = RiskLimits.Default;

        // Kostenfilter: Einstiege ablehnen, deren Stop zu eng fuer den Spread ist. 100 heisst
        // aus. Der Wert ist der Anteil des Risikos, den Spread und Kommission verbrauchen duerfen.
        limits.MaxCostShareOfRiskPercent = arguments.GetDecimal("max-cost-share", 100m);

        // Positionsfuehrung: wirkt auf alle Strategien gleich, deshalb hier und nicht je Strategie.
        limits.BreakevenAfterR = arguments.GetDecimal("breakeven-r", 0m);
        limits.TrailStopDistanceR = arguments.GetDecimal("trail-r", 0m);
        limits.TrailStartsAfterR = arguments.GetDecimal("trail-start-r", 1m);
        limits.Validate();

        // Kostenannahmen skalieren: Die Spread-Angaben der Konfiguration sind Annahmen, solange
        // sie nicht aus dem Export des eigenen Brokers stammen. Mit --spread-factor lässt sich
        // messen, wie sehr ein Ergebnis an dieser Annahme hängt - bei kurzen Haltedauern ist das
        // oft die wichtigste Frage überhaupt.
        var spreadFactor = arguments.GetDecimal("spread-factor", 1m);
        if (spreadFactor <= 0m)
        {
            throw new ArgumentException("--spread-factor muss größer als 0 sein.");
        }

        if (spreadFactor != 1m)
        {
            foreach (var symbol in symbols)
            {
                symbol.Config.TypicalSpread *= spreadFactor;
            }

            notes.Add($"Spread-Annahmen mit Faktor {spreadFactor} skaliert (--spread-factor).");
            Console.WriteLine($"Spread-Annahmen mit Faktor {spreadFactor} skaliert.");
        }

        var options = new BacktestOptions
        {
            StartingBalance = arguments.GetDecimal("balance", 10_000m),
            SlippageTicks = arguments.GetDecimal("slippage", 1m),
            StopSlippageTicks = arguments.GetDecimal("stop-slippage", 3m),
            RandomSeed = arguments.GetInt("seed", 20240301),
            UseLimitEntries = arguments.Has("limit-entries"),
            MinimumSampleTrades = arguments.GetInt("min-trades", 30),
        };

        var strategies = arguments.Has("strategy")
            ? new[] { StrategyRegistry.Get(arguments.Get("strategy", string.Empty)) }
            : StrategyRegistry.All.ToArray();

        var writer = new ResultWriter(arguments.Get("results", "results"));

        Console.WriteLine();
        Console.WriteLine($"Rechne {symbols.Count} Symbol(e) x {strategies.Length} Strategie(n) ...");

        var matrix = new MatrixRunner(limits, options).Run(symbols, strategies);
        writer.WriteMatrix(matrix.Results);

        var regimeRows = new List<RegimeResult>();
        foreach (var symbol in symbols)
        {
            var regimes = RegimeAnalysis.TagDays(symbol.Bars);
            foreach (var result in matrix.Results.Where(result => result.Symbol == symbol.Config.Name))
            {
                if (result.Trades.Count > 0)
                {
                    writer.WriteTrades(result);
                    regimeRows.AddRange(RegimeAnalysis.Evaluate(result, regimes));
                }
            }
        }

        writer.WriteRegimes(regimeRows);

        var reports = new List<WalkForwardReport>();
        if (walkForward)
        {
            var runner = new WalkForwardRunner(limits, options);
            foreach (var symbol in symbols)
            {
                foreach (var strategy in strategies)
                {
                    var report = runner.Run(
                        symbol, strategy, arguments.GetInt("train", 180), arguments.GetInt("test", 60));
                    reports.Add(report);

                    if (report.Profile != null)
                    {
                        writer.WriteProfile(report.Profile);
                    }

                    notes.AddRange(report.Warnings.Select(warning => $"{strategy.Name} / {symbol.Config.Name}: {warning}"));
                }
            }

            writer.WriteWalkForward(reports);
        }

        writer.WriteSummary(matrix, reports, options, notes);

        // Jede Zerlegung gegen den Muenzwurf. Ohne diesen Massstab erzeugt das Durchsehen
        // vieler Gruppen zuverlaessig Scheinfunde.
        var allTrades = matrix.Results.SelectMany(result => result.Trades).ToList();
        var baselinePath = Path.Combine(arguments.Get("results", "results"), "baseline.md");
        File.WriteAllText(baselinePath, BaselineAnalysis.Report(allTrades));

        Console.WriteLine();
        Console.WriteLine($"{matrix.CombinationsTested} Kombinationen in {matrix.Duration.TotalSeconds:0.0} s gerechnet.");
        Console.WriteLine($"Matrix:        {writer.MatrixPath}");
        Console.WriteLine($"Muenzwurf-Vergleich: {baselinePath}");
        Console.WriteLine($"Regime-Tabelle:{writer.RegimePath}");
        if (walkForward)
        {
            Console.WriteLine($"Walk-Forward:  {writer.WalkForwardPath}");
        }

        Console.WriteLine($"Zusammenfassung: {writer.SummaryPath}");

        foreach (var best in matrix.Results
                     .Where(result => result.Metrics.TradeCount > 0)
                     .OrderByDescending(result => result.Metrics.ExpectancyR)
                     .Take(5))
        {
            Console.WriteLine(
                $"  {best.StrategyKey,-28} {best.Symbol,-8} {best.Metrics.TradeCount,5} Trades  " +
                $"E={best.Metrics.ExpectancyR,7:0.###} R  netto={best.Metrics.NetPnL,10:0.00}" +
                (best.Metrics.IsStatisticallyWeak ? "  [zu wenige Trades]" : string.Empty));
        }
    }

    private static async Task<(IReadOnlyList<SymbolData> Symbols, List<string> Notes)> LoadAsync(CommandLine arguments)
    {
        var catalog = SymbolCatalog.Load(arguments.Get("config", "config/symbols.json"));
        var dataRoot = arguments.Get("data", "data");
        var from = arguments.GetDate("from", DateTime.UtcNow.Date.AddYears(-1));
        var to = arguments.GetDate("to", DateTime.UtcNow.Date);

        var store = new ParquetBarStore(Path.Combine(dataRoot, "normalized"));
        var pipeline = new DataPipeline(store, Path.Combine(dataRoot, "manifests"), Path.Combine(arguments.Get("results", "results"), "data-quality"));

        var symbols = new List<SymbolData>();
        var notes = new List<string>();

        foreach (var config in Selected(catalog, arguments))
        {
            var manifestPath = Path.Combine(dataRoot, "manifests", $"{config.Name}.json");
            var stored = File.Exists(manifestPath) ? SymbolManifest.Load(manifestPath) : null;

            if (stored == null)
            {
                notes.Add($"{config.Name}: kein Manifest gefunden - erst 'ingest' laufen lassen.");
                continue;
            }

            if (!string.Equals(stored.QualityStatus, DataQualityStatus.Rejected.ToString(), StringComparison.Ordinal))
            {
                var timeframe = Timeframe.Parse(stored.StoredTimeframe);
                var bars = await pipeline.LoadAsync(config, timeframe, from, to);
                symbols.Add(new SymbolData(config, bars));
                Console.WriteLine($"{config.Name}: {bars.Count} Bars geladen (Qualitaet {stored.QualityStatus}).");
            }
            else
            {
                notes.Add($"{config.Name}: wegen Datenqualitaet ausgeschlossen ({stored.MissingPercent:0.00} % fehlend).");
                Console.WriteLine($"{config.Name}: ausgeschlossen, Datenqualitaet {stored.QualityStatus}.");
            }
        }

        return (symbols, notes);
    }

    /// <summary>FNV-1a: über Prozesse und Plattformen hinweg stabil, anders als string.GetHashCode().</summary>
    private static int StableHash(string value)
    {
        unchecked
        {
            var hash = 2166136261u;
            foreach (var character in value)
            {
                hash = (hash ^ character) * 16777619u;
            }

            return (int)(hash % 1000u);
        }
    }

    private static IEnumerable<SymbolConfig> Selected(SymbolCatalog catalog, CommandLine arguments)
    {
        if (!arguments.Has("symbol"))
        {
            return catalog.Symbols;
        }

        var wanted = arguments.Get("symbol", string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        return wanted.Select(name => catalog[name]).ToList();
    }
}
