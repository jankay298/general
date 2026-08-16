using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Daytrading.Data.Config;
using Daytrading.Execution.Sessions;
using Daytrading.Data.Normalization;
using Daytrading.Data.Providers;
using Daytrading.Data.Quality;
using Daytrading.Data.Storage;
using Daytrading.Strategies.Model;

namespace Daytrading.Data;

/// <summary>Ergebnis eines Pipelinelaufs für ein Symbol.</summary>
public sealed class IngestResult
{
    public IngestResult(SymbolManifest manifest, DataQualityReport report, string reportPath)
    {
        Manifest = manifest;
        Report = report;
        ReportPath = reportPath;
    }

    public SymbolManifest Manifest { get; }

    public DataQualityReport Report { get; }

    public string ReportPath { get; }

    public bool IsUsable => Report.IsUsable;
}

/// <summary>
/// Der Weg von der Quelle bis zu backtestfähigen Daten: laden, normalisieren, prüfen,
/// speichern, dokumentieren.
/// </summary>
/// <remarks>
/// Jeder Schritt hinterlässt eine Spur. Der Qualitätsreport entscheidet, ob ein Symbol in den
/// Backtest darf; abgelehnte Symbole werden benannt, statt stillschweigend zu fehlen.
/// Gespeichert wird immer in der feinsten Auflösung der Quelle - gröbere Zeitrahmen entstehen
/// beim Laden durch Aggregation.
/// </remarks>
public sealed class DataPipeline
{
    private readonly IBarStore _store;
    private readonly string _manifestDirectory;
    private readonly string _reportDirectory;
    private readonly DataQualityAnalyzer _analyzer;

    public DataPipeline(
        IBarStore store,
        string manifestDirectory,
        string reportDirectory,
        DataQualityThresholds? thresholds = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _manifestDirectory = manifestDirectory ?? throw new ArgumentNullException(nameof(manifestDirectory));
        _reportDirectory = reportDirectory ?? throw new ArgumentNullException(nameof(reportDirectory));
        _analyzer = new DataQualityAnalyzer(thresholds);
    }

    public async Task<IngestResult> IngestAsync(
        SymbolConfig config,
        IDataProvider provider,
        DateTime fromUtc,
        DateTime toUtc,
        CancellationToken cancellationToken = default)
    {
        if (config == null)
        {
            throw new ArgumentNullException(nameof(config));
        }

        if (provider == null)
        {
            throw new ArgumentNullException(nameof(provider));
        }

        config.Validate();

        var request = new DataRequest(config.SourceName, provider.NativeTimeframe, fromUtc, toUtc);
        var raw = await provider.GetBarsAsync(request, cancellationToken).ConfigureAwait(false);
        var bars = BarNormalizer.Normalize(raw, out var normalization);

        var calendar = new SymbolSessionCalendar(config);
        var report = _analyzer.Analyze(
            config.Name, provider.Name, provider.NativeTimeframe, bars, calendar, fromUtc, toUtc, normalization);

        if (bars.Count > 0)
        {
            await _store.WriteAsync(config.Name, provider.NativeTimeframe, bars, cancellationToken).ConfigureAwait(false);
        }

        var manifest = new SymbolManifest
        {
            Symbol = config.Name,
            SourceSymbol = config.SourceName,
            Source = provider.Name,
            StoredTimeframe = provider.NativeTimeframe.ToString(),
            WorkingTimeframe = config.WorkingTimeframe,
            FromUtc = fromUtc,
            ToUtc = toUtc,
            DownloadedAtUtc = DateTime.UtcNow,
            BarCount = bars.Count,
            QualityStatus = report.Status.ToString(),
            MissingPercent = report.MissingPercent,
            MeasuredMedianSpread = (provider as OandaDataProvider)?.LastSpreadStatistics?.Median,
            Adjustment = config.AssetClass == AssetClass.Equity ? "splitbereinigt (Quelle bestätigen)" : "nicht zutreffend",
        };

        Directory.CreateDirectory(_manifestDirectory);
        Directory.CreateDirectory(_reportDirectory);

        manifest.Save(Path.Combine(_manifestDirectory, $"{config.Name}.json"));
        var reportPath = Path.Combine(_reportDirectory, $"{config.Name}.md");
        File.WriteAllText(reportPath, report.ToMarkdown());

        return new IngestResult(manifest, report, reportPath);
    }

    /// <summary>
    /// Lädt die gespeicherten Bars und aggregiert sie auf die Arbeitsauflösung des Symbols.
    /// Das ist der Weg, auf dem der Backtester an seine Daten kommt.
    /// </summary>
    public async Task<IReadOnlyList<Candle>> LoadAsync(
        SymbolConfig config,
        Timeframe storedTimeframe,
        DateTime fromUtc,
        DateTime toUtc,
        CancellationToken cancellationToken = default)
    {
        var bars = await _store.ReadAsync(config.Name, storedTimeframe, fromUtc, toUtc, cancellationToken).ConfigureAwait(false);
        var working = config.ToTimeframe();
        return working == storedTimeframe ? bars : BarAggregator.Aggregate(bars, storedTimeframe, working);
    }
}
