using Daytrading.Data;
using Daytrading.Data.Config;
using Daytrading.Data.Providers;
using Daytrading.Data.Quality;
using Daytrading.Data.Storage;
using Daytrading.Strategies.Model;

namespace Daytrading.Data.Tests;

/// <summary>Temporäres Arbeitsverzeichnis, das sich nach dem Test selbst aufräumt.</summary>
internal sealed class TempDirectory : IDisposable
{
    public TempDirectory()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "daytrading-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string Combine(params string[] parts) => System.IO.Path.Combine(new[] { Path }.Concat(parts).ToArray());

    public void Dispose()
    {
        if (Directory.Exists(Path))
        {
            Directory.Delete(Path, recursive: true);
        }
    }
}

public class ParquetBarStoreTests
{
    private static List<Candle> Series(DateTime start, int count)
    {
        var bars = new List<Candle>(count);
        var price = 100m;
        for (var i = 0; i < count; i++)
        {
            bars.Add(new Candle(start.AddMinutes(i), price, price + 0.25m, price - 0.25m, price + 0.1m, 5m));
            price += 0.05m;
        }

        return bars;
    }

    [Fact]
    public async Task Writes_and_reads_bars_without_losing_precision_or_the_time_zone()
    {
        using var directory = new TempDirectory();
        var store = new ParquetBarStore(directory.Path);
        var bars = Series(new DateTime(2024, 3, 1, 14, 0, 0, DateTimeKind.Utc), 100);

        await store.WriteAsync("BTCUSD", Timeframe.M1, bars);
        var read = await store.ReadAsync(
            "BTCUSD", Timeframe.M1, new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc), new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc));

        Assert.Equal(bars.Count, read.Count);
        Assert.Equal(bars[0], read[0]);
        Assert.Equal(bars[^1], read[^1]);
        Assert.All(read, bar => Assert.Equal(DateTimeKind.Utc, bar.OpenTimeUtc.Kind));
    }

    [Fact]
    public async Task Partitions_by_year()
    {
        using var directory = new TempDirectory();
        var store = new ParquetBarStore(directory.Path);
        var bars = new List<Candle>();
        bars.AddRange(Series(new DateTime(2023, 12, 31, 23, 0, 0, DateTimeKind.Utc), 60));
        bars.AddRange(Series(new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc), 60));

        await store.WriteAsync("BTCUSD", Timeframe.M1, bars);

        var files = Directory.GetFiles(directory.Combine("BTCUSD", "M1"), "*.parquet").Select(Path.GetFileName).ToList();
        Assert.Contains("2023.parquet", files);
        Assert.Contains("2024.parquet", files);
        Assert.True(store.Contains("BTCUSD", Timeframe.M1));
    }

    [Fact]
    public async Task Reads_only_the_requested_range()
    {
        using var directory = new TempDirectory();
        var store = new ParquetBarStore(directory.Path);
        var start = new DateTime(2024, 3, 1, 14, 0, 0, DateTimeKind.Utc);
        await store.WriteAsync("BTCUSD", Timeframe.M1, Series(start, 60));

        var read = await store.ReadAsync("BTCUSD", Timeframe.M1, start.AddMinutes(10), start.AddMinutes(20));

        Assert.Equal(10, read.Count);
        Assert.Equal(start.AddMinutes(10), read[0].OpenTimeUtc);
    }

    [Fact]
    public async Task Says_clearly_when_a_symbol_was_never_ingested()
    {
        using var directory = new TempDirectory();
        var store = new ParquetBarStore(directory.Path);

        var error = await Assert.ThrowsAsync<MarketDataException>(() => store.ReadAsync(
            "NIEGELADEN", Timeframe.M1, new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc), new DateTime(2024, 2, 1, 0, 0, 0, DateTimeKind.Utc)));

        Assert.Contains("Pipeline", error.Message);
        Assert.False(store.Contains("NIEGELADEN", Timeframe.M1));
    }
}

public class SymbolManifestTests
{
    [Fact]
    public void Round_trips_through_json()
    {
        using var directory = new TempDirectory();
        var path = directory.Combine("BTCUSD.json");
        var manifest = new SymbolManifest
        {
            Symbol = "BTCUSD",
            Source = "binance-public-data",
            StoredTimeframe = "M1",
            WorkingTimeframe = "M5",
            FromUtc = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            ToUtc = new DateTime(2024, 2, 1, 0, 0, 0, DateTimeKind.Utc),
            BarCount = 44_640,
            QualityStatus = "Good",
            MeasuredMedianSpread = 2.5m,
        };

        manifest.Save(path);
        var loaded = SymbolManifest.Load(path);

        Assert.Equal(manifest.Symbol, loaded.Symbol);
        Assert.Equal(manifest.BarCount, loaded.BarCount);
        Assert.Equal(2.5m, loaded.MeasuredMedianSpread);
        Assert.Equal(manifest.FromUtc, loaded.FromUtc);
    }
}

public class DataPipelineTests
{
    private sealed class StubProvider : IDataProvider
    {
        private readonly IReadOnlyList<Candle> _bars;

        public StubProvider(IReadOnlyList<Candle> bars)
        {
            _bars = bars;
        }

        public string Name => "stub";

        public Timeframe NativeTimeframe => Timeframe.M1;

        public Task<IReadOnlyList<Candle>> GetBarsAsync(DataRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(_bars);
    }

    private static SymbolConfig Crypto() => new SymbolConfig
    {
        Name = "BTCUSD",
        SourceSymbol = "BTCUSDT",
        AssetClass = AssetClass.Crypto,
        TimeZone = "UTC",
        SessionStart = "00:00",
        SessionEnd = "24:00",
        WorkingTimeframe = "M5",
        TickSize = 0.01m,
        MinQuantity = 0.00001m,
        QuantityStep = 0.00001m,
    };

    private static List<Candle> OneDayOfMinutes(DateTime day)
    {
        var bars = new List<Candle>(1440);
        var price = 50_000m;
        for (var i = 0; i < 1440; i++)
        {
            bars.Add(new Candle(day.AddMinutes(i), price, price + 5m, price - 5m, price + 1m, 10m));
            price += 0.5m;
        }

        return bars;
    }

    [Fact]
    public async Task Ingests_stores_and_documents_a_symbol_end_to_end()
    {
        using var directory = new TempDirectory();
        var day = new DateTime(2024, 3, 1, 0, 0, 0, DateTimeKind.Utc);
        var store = new ParquetBarStore(directory.Combine("normalized"));
        var pipeline = new DataPipeline(store, directory.Combine("manifests"), directory.Combine("reports"));

        var result = await pipeline.IngestAsync(
            Crypto(), new StubProvider(OneDayOfMinutes(day)), day, day.AddDays(1));

        Assert.Equal(DataQualityStatus.Good, result.Report.Status);
        Assert.Equal(1440, result.Manifest.BarCount);
        Assert.Equal("M1", result.Manifest.StoredTimeframe);
        Assert.Equal("M5", result.Manifest.WorkingTimeframe);
        Assert.True(File.Exists(directory.Combine("manifests", "BTCUSD.json")));
        Assert.True(File.Exists(result.ReportPath));
        Assert.True(store.Contains("BTCUSD", Timeframe.M1));
    }

    [Fact]
    public async Task Loads_stored_minutes_and_aggregates_them_to_the_working_timeframe()
    {
        using var directory = new TempDirectory();
        var day = new DateTime(2024, 3, 1, 0, 0, 0, DateTimeKind.Utc);
        var store = new ParquetBarStore(directory.Combine("normalized"));
        var pipeline = new DataPipeline(store, directory.Combine("manifests"), directory.Combine("reports"));
        var config = Crypto();

        await pipeline.IngestAsync(config, new StubProvider(OneDayOfMinutes(day)), day, day.AddDays(1));
        var bars = await pipeline.LoadAsync(config, Timeframe.M1, day, day.AddDays(1));

        Assert.Equal(288, bars.Count);   // 1440 Minuten / 5
        Assert.Equal(Timeframe.M5.Duration, bars[1].OpenTimeUtc - bars[0].OpenTimeUtc);
    }

    [Fact]
    public async Task Reports_a_gap_instead_of_filling_it()
    {
        using var directory = new TempDirectory();
        var day = new DateTime(2024, 3, 1, 0, 0, 0, DateTimeKind.Utc);
        var bars = OneDayOfMinutes(day);
        bars.RemoveRange(600, 30);

        var pipeline = new DataPipeline(
            new ParquetBarStore(directory.Combine("normalized")),
            directory.Combine("manifests"),
            directory.Combine("reports"));

        var result = await pipeline.IngestAsync(Crypto(), new StubProvider(bars), day, day.AddDays(1));

        Assert.Equal(30, result.Report.MissingBars);
        Assert.Equal(1410, result.Manifest.BarCount);
        Assert.Single(result.Report.LargestGaps);
        Assert.Contains("fehlen", File.ReadAllText(result.ReportPath));
    }
}
