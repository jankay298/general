using System.IO.Compression;
using System.Text;
using Daytrading.Data;
using Daytrading.Data.Config;
using Daytrading.Data.Normalization;
using Daytrading.Data.Providers;
using Daytrading.Data.Quality;
using Daytrading.Strategies.Model;

namespace Daytrading.Data.Tests;

public class DataQualityAnalyzerTests
{
    private static SymbolConfig Equity() => new SymbolConfig
    {
        Name = "AAPL",
        AssetClass = AssetClass.Equity,
        TimeZone = "America/New_York",
        SessionStart = "09:30",
        SessionEnd = "16:00",
        HolidayCalendar = "us-equity",
        WorkingTimeframe = "M15",
    };

    private static readonly DateTime From = new DateTime(2024, 7, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime To = new DateTime(2024, 7, 6, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>Erzeugt eine lückenlose Serie über alle Sessions des Zeitraums.</summary>
    private static List<Candle> CompleteSeries(SymbolConfig config, Timeframe timeframe)
    {
        var calendar = new SymbolSessionCalendar(config);
        var bars = new List<Candle>();
        var price = 100m;

        foreach (var session in calendar.Sessions(From, To))
        {
            for (var time = session.StartUtc; time + timeframe.Duration <= session.EndUtc; time += timeframe.Duration)
            {
                if (time < From || time >= To)
                {
                    continue;
                }

                bars.Add(new Candle(time, price, price + 0.5m, price - 0.5m, price, 1_000m));
                price += 0.01m;
            }
        }

        return bars;
    }

    private static DataQualityReport Analyze(
        List<Candle> bars,
        SymbolConfig? config = null,
        DataQualityThresholds? thresholds = null,
        NormalizationReport? normalization = null)
    {
        config ??= Equity();
        var settings = thresholds ?? new DataQualityThresholds { MinimumBars = 10 };
        return new DataQualityAnalyzer(settings).Analyze(
            config.Name, "test", Timeframe.M15, bars, new SymbolSessionCalendar(config), From, To, normalization);
    }

    [Fact]
    public void A_complete_series_passes()
    {
        var report = Analyze(CompleteSeries(Equity(), Timeframe.M15));

        Assert.Equal(DataQualityStatus.Good, report.Status);
        Assert.Equal(0, report.MissingBars);
        Assert.True(report.IsUsable);
    }

    [Fact]
    public void Independence_day_is_a_holiday_and_not_a_gap()
    {
        var report = Analyze(CompleteSeries(Equity(), Timeframe.M15));

        // 1.-5. Juli 2024: Do 4.7. Feiertag, also vier Handelstage statt fünf.
        Assert.Equal(4, report.TradingDays);
        Assert.Equal(1, report.HolidaysSkipped);
        Assert.Equal(0, report.MissingBars);
    }

    [Fact]
    public void Missing_bars_are_counted_and_grouped_into_gaps()
    {
        var bars = CompleteSeries(Equity(), Timeframe.M15);
        var removed = bars.GetRange(10, 4);
        bars.RemoveRange(10, 4);

        var report = Analyze(bars);

        Assert.Equal(4, report.MissingBars);
        var gap = Assert.Single(report.LargestGaps);
        Assert.Equal(4, gap.MissingBars);
        Assert.Equal(removed[0].OpenTimeUtc, gap.FromUtc);
    }

    [Fact]
    public void Too_many_missing_bars_disqualify_the_symbol()
    {
        var bars = CompleteSeries(Equity(), Timeframe.M15);
        bars.RemoveRange(0, bars.Count / 2);

        var report = Analyze(bars);

        Assert.Equal(DataQualityStatus.Rejected, report.Status);
        Assert.False(report.IsUsable);
        Assert.Contains(report.Findings, finding => finding.Contains("fehlen"));
    }

    [Fact]
    public void Conflicting_duplicates_disqualify_the_symbol_outright()
    {
        var bars = CompleteSeries(Equity(), Timeframe.M15);
        var raw = new List<Candle>(bars)
        {
            // Gleicher Zeitstempel, anderer Kurs.
            new Candle(bars[0].OpenTimeUtc, 999m, 1_000m, 998m, 999m, 1m),
        };

        var normalized = BarNormalizer.Normalize(raw, out var normalization);
        var report = Analyze(normalized.ToList(), normalization: normalization);

        Assert.Equal(DataQualityStatus.Rejected, report.Status);
        Assert.Contains(report.Findings, finding => finding.Contains("vermischte Quellen"));
    }

    [Fact]
    public void An_empty_series_is_rejected_with_a_clear_finding()
    {
        var report = Analyze(new List<Candle>());

        Assert.Equal(DataQualityStatus.Rejected, report.Status);
        Assert.Contains(report.Findings, finding => finding.Contains("Keine einzige Bar"));
    }

    [Fact]
    public void Too_few_bars_are_rejected_even_when_nothing_is_missing()
    {
        var bars = CompleteSeries(Equity(), Timeframe.M15);

        var report = Analyze(bars, thresholds: new DataQualityThresholds { MinimumBars = 100_000 });

        Assert.Equal(DataQualityStatus.Rejected, report.Status);
        Assert.Contains(report.Findings, finding => finding.Contains("mindestens"));
    }

    [Fact]
    public void Outlier_bars_are_flagged_but_not_removed()
    {
        var bars = CompleteSeries(Equity(), Timeframe.M15);
        var bad = bars[20];
        bars[20] = new Candle(bad.OpenTimeUtc, bad.Open, bad.Open + 500m, bad.Open - 500m, bad.Close, bad.Volume);

        var report = Analyze(bars);

        Assert.Equal(1, report.OutlierBars);
        Assert.Contains(report.Findings, finding => finding.Contains("Spanne über dem"));
        Assert.Equal(bars.Count, report.BarCount);   // nichts entfernt, nur gemeldet
    }

    [Fact]
    public void Bars_outside_the_session_are_reported_separately()
    {
        var bars = CompleteSeries(Equity(), Timeframe.M15);
        bars.Insert(0, new Candle(new DateTime(2024, 7, 1, 11, 0, 0, DateTimeKind.Utc), 100m, 101m, 99m, 100m, 10m));

        var report = Analyze(bars);

        Assert.Equal(1, report.BarsOutsideSession);
        Assert.Equal(0, report.MissingBars);
    }

    [Fact]
    public void The_markdown_report_names_the_essentials()
    {
        var markdown = Analyze(CompleteSeries(Equity(), Timeframe.M15)).ToMarkdown();

        Assert.Contains("Datenqualität: AAPL", markdown);
        Assert.Contains("Status", markdown);
        Assert.Contains("nicht** interpoliert", markdown);
    }
}

public class KlineCsvParserTests
{
    [Fact]
    public void Reads_binance_klines()
    {
        var csv = "1709294400000,100.1,101.5,99.8,100.9,12.5,1709294459999,0,0,0,0,0\n";

        var bars = new List<Candle>();
        KlineCsvParser.Parse(new StringReader(csv), bars);

        var bar = Assert.Single(bars);
        Assert.Equal(new DateTime(2024, 3, 1, 12, 0, 0, DateTimeKind.Utc), bar.OpenTimeUtc);
        Assert.Equal(100.1m, bar.Open);
        Assert.Equal(101.5m, bar.High);
        Assert.Equal(99.8m, bar.Low);
        Assert.Equal(100.9m, bar.Close);
        Assert.Equal(12.5m, bar.Volume);
    }

    [Fact]
    public void Skips_the_header_row_that_newer_archives_carry()
    {
        var csv = "open_time,open,high,low,close,volume\n1709294400000,100,101,99,100,1\n";

        var bars = new List<Candle>();
        KlineCsvParser.Parse(new StringReader(csv), bars);

        Assert.Single(bars);
    }

    [Theory]
    [InlineData(1709294400L)]              // Sekunden
    [InlineData(1709294400000L)]           // Millisekunden
    [InlineData(1709294400000000L)]        // Mikrosekunden
    public void Recognises_the_timestamp_precision_from_its_magnitude(long epoch)
    {
        // Binance hat die Präzision im Lauf der Zeit gewechselt. Ohne Erkennung landen
        // Bars stillschweigend im falschen Jahrtausend.
        Assert.Equal(new DateTime(2024, 3, 1, 12, 0, 0, DateTimeKind.Utc), KlineCsvParser.FromEpoch(epoch));
    }

    [Fact]
    public void Complains_about_broken_lines_instead_of_skipping_them()
    {
        var csv = "1709294400000,100,101,99,100,1\n1709294460000,100,kaputt,99,100,1\n";

        var bars = new List<Candle>();
        var error = Assert.Throws<MarketDataException>(() => KlineCsvParser.Parse(new StringReader(csv), bars));

        Assert.Contains("Zeile 2", error.Message);
    }
}

public class BinancePublicDataProviderTests
{
    private sealed class FakeDownloader : IFileDownloader
    {
        private readonly Dictionary<string, byte[]> _files = new(StringComparer.OrdinalIgnoreCase);

        public List<string> RequestedUrls { get; } = new();

        public void Add(string urlPart, string csv)
        {
            using var buffer = new MemoryStream();
            using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
            {
                var entry = archive.CreateEntry("data.csv");
                using var stream = entry.Open();
                var bytes = Encoding.UTF8.GetBytes(csv);
                stream.Write(bytes, 0, bytes.Length);
            }

            _files[urlPart] = buffer.ToArray();
        }

        public Task<byte[]?> TryDownloadAsync(string url, CancellationToken cancellationToken = default)
        {
            RequestedUrls.Add(url);
            foreach (var pair in _files)
            {
                if (url.Contains(pair.Key, StringComparison.OrdinalIgnoreCase))
                {
                    return Task.FromResult<byte[]?>(pair.Value);
                }
            }

            return Task.FromResult<byte[]?>(null);
        }
    }

    [Fact]
    public async Task Reads_monthly_archives_and_keeps_only_the_requested_range()
    {
        var downloader = new FakeDownloader();
        downloader.Add(
            "BTCUSDT-1m-2024-01.zip",
            string.Join("\n", new[]
            {
                "1704067200000,100,101,99,100,1",   // 2024-01-01 00:00
                "1704067260000,100,101,99,100,1",   // 2024-01-01 00:01
                "1704153600000,100,101,99,100,1",   // 2024-01-02 00:00
            }));

        var provider = new BinancePublicDataProvider(downloader);
        var bars = await provider.GetBarsAsync(new DataRequest(
            "BTCUSDT",
            Timeframe.M1,
            new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            new DateTime(2024, 1, 2, 0, 0, 0, DateTimeKind.Utc)));

        Assert.Equal(2, bars.Count);
        Assert.Contains("monthly/klines/BTCUSDT/1m", downloader.RequestedUrls[0]);
    }

    [Fact]
    public async Task Records_archives_that_do_not_exist_instead_of_failing()
    {
        var provider = new BinancePublicDataProvider(new FakeDownloader());

        var bars = await provider.GetBarsAsync(new DataRequest(
            "BTCUSDT",
            Timeframe.M1,
            new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            new DateTime(2024, 2, 1, 0, 0, 0, DateTimeKind.Utc)));

        Assert.Empty(bars);
        Assert.Contains("BTCUSDT-1m-2024-01.zip", provider.MissingArchives);
    }

    [Fact]
    public async Task Caches_downloads_so_a_second_run_does_not_fetch_again()
    {
        var directory = Path.Combine(Path.GetTempPath(), "binance-cache-" + Guid.NewGuid().ToString("N"));
        try
        {
            var downloader = new FakeDownloader();
            downloader.Add("BTCUSDT-1m-2024-01.zip", "1704067200000,100,101,99,100,1");
            var request = new DataRequest(
                "BTCUSDT",
                Timeframe.M1,
                new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                new DateTime(2024, 2, 1, 0, 0, 0, DateTimeKind.Utc));

            await new BinancePublicDataProvider(downloader, directory).GetBarsAsync(request);
            var afterFirst = downloader.RequestedUrls.Count;
            await new BinancePublicDataProvider(downloader, directory).GetBarsAsync(request);

            Assert.Equal(afterFirst, downloader.RequestedUrls.Count);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task Refuses_resolutions_binance_does_not_offer()
    {
        var provider = new BinancePublicDataProvider(new FakeDownloader());

        var request = new DataRequest(
            "BTCUSDT",
            Timeframe.FromMinutes(7),
            new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            new DateTime(2024, 2, 1, 0, 0, 0, DateTimeKind.Utc));

        await Assert.ThrowsAsync<MarketDataException>(() => provider.GetBarsAsync(request));
    }
}
