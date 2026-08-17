using Daytrading.Data;
using Daytrading.Data.Providers;
using Daytrading.Strategies.Model;

namespace Daytrading.Data.Tests;

/// <summary>
/// Tests des Dukascopy-Providers.
/// </summary>
/// <remarks>
/// Die Testdaten werden mit demselben LZMA-Format erzeugt, das Dukascopy ausliefert - der
/// Dekoder wird also gegen echtes LZMA geprüft, nur ohne Netzzugriff.
///
/// Der Satzaufbau selbst stammt aus einem Abruf gegen den echten Server: XAUUSD, 12.06.2024,
/// 10:00 UTC ergab Bid 2313.945 und Ask 2314.312 - dieselben Werte wie am Tick-Endpunkt.
/// Daher bilden die Fixtures die Kurse als skalierte Ganzzahlen ab und nicht als float.
/// </remarks>
public class DukascopyProviderTests
{
    private const string Symbol = "XAUUSD";
    private static readonly DateTime Day = new DateTime(2024, 6, 12, 0, 0, 0, DateTimeKind.Utc);

    private sealed class FakeDownloader : IFileDownloader
    {
        private readonly Dictionary<string, byte[]> _files = new(StringComparer.OrdinalIgnoreCase);

        public List<string> RequestedUrls { get; } = new();

        public void Add(string urlPart, byte[] payload) => _files[urlPart] = payload;

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

    /// <summary>
    /// Baut eine Datei im Format von Dukascopy: LZMA-alone über 24-Byte-Sätzen, big endian,
    /// Kurse als skalierte Ganzzahlen, Volumen als float32.
    /// </summary>
    /// <remarks>
    /// Das Format ist am echten Feed nachgemessen (XAUUSD, 12.06.2024). Die Kurse sind der
    /// Fallstrick: als float gelesen ergeben sie 0.
    /// </remarks>
    private static byte[] BuildFile(params (int Seconds, int Open, int Close, int Low, int High, float Volume)[] rows)
    {
        var raw = new byte[rows.Length * 24];
        for (var i = 0; i < rows.Length; i++)
        {
            var offset = i * 24;
            WriteUInt32(raw, offset, (uint)rows[i].Seconds);
            WriteUInt32(raw, offset + 4, (uint)rows[i].Open);
            WriteUInt32(raw, offset + 8, (uint)rows[i].Close);
            WriteUInt32(raw, offset + 12, (uint)rows[i].Low);
            WriteUInt32(raw, offset + 16, (uint)rows[i].High);
            WriteSingle(raw, offset + 20, rows[i].Volume);
        }

        using var input = new MemoryStream(raw);
        using var output = new MemoryStream();
        var encoder = new SevenZip.Compression.LZMA.Encoder();
        encoder.WriteCoderProperties(output);
        output.Write(BitConverter.GetBytes((long)raw.Length), 0, 8);
        encoder.Code(input, output, raw.Length, -1, null);
        return output.ToArray();
    }

    private static void WriteUInt32(byte[] target, int offset, uint value)
    {
        target[offset] = (byte)(value >> 24);
        target[offset + 1] = (byte)(value >> 16);
        target[offset + 2] = (byte)(value >> 8);
        target[offset + 3] = (byte)value;
    }

    private static void WriteSingle(byte[] target, int offset, float value)
    {
        var bytes = BitConverter.GetBytes(value);
        target[offset] = bytes[3];
        target[offset + 1] = bytes[2];
        target[offset + 2] = bytes[1];
        target[offset + 3] = bytes[0];
    }

    private static DataRequest Request(int days = 1) => new DataRequest(Symbol, Timeframe.M1, Day, Day.AddDays(days));

    [Fact]
    public async Task Decodes_the_lzma_format_and_rebuilds_minute_bars()
    {
        var downloader = new FakeDownloader();
        downloader.Add("BID_candles_min_1", BuildFile(
            (0, 2313000, 2313500, 2312800, 2313600, 120f),
            (60, 2313500, 2314000, 2313400, 2314200, 95f)));

        var bars = await new DukascopyDataProvider(downloader).GetBarsAsync(Request());

        Assert.Equal(2, bars.Count);
        Assert.Equal(Day, bars[0].OpenTimeUtc);
        Assert.Equal(Day.AddMinutes(1), bars[1].OpenTimeUtc);
        Assert.Equal(2313m, bars[0].Open);      // Skalierung 1000
        Assert.Equal(2313.6m, bars[0].High);
        Assert.Equal(2312.8m, bars[0].Low);
        Assert.Equal(2313.5m, bars[0].Close);
    }

    [Fact]
    public async Task Measures_the_spread_from_bid_and_ask_instead_of_guessing_it()
    {
        // Der eigentliche Grund für diese Quelle: der Spread wird gemessen, nicht angenommen.
        var downloader = new FakeDownloader();
        downloader.Add("BID_candles_min_1", BuildFile((0, 2313000, 2313000, 2313000, 2313000, 10f)));
        downloader.Add("ASK_candles_min_1", BuildFile((0, 2313400, 2313400, 2313400, 2313400, 10f)));

        var provider = new DukascopyDataProvider(downloader);
        var bars = await provider.GetBarsAsync(Request());

        var bar = Assert.Single(bars);
        Assert.Equal(2313.2m, bar.Close);   // Mittelkurs zwischen Geld und Brief
        Assert.NotNull(provider.LastSpreadStatistics);
        Assert.Equal(0.4m, provider.LastSpreadStatistics!.Median);
    }

    [Fact]
    public async Task Uses_the_bid_series_alone_when_no_ask_file_exists()
    {
        var downloader = new FakeDownloader();
        downloader.Add("BID_candles_min_1", BuildFile((0, 2313000, 2313500, 2312800, 2313600, 10f)));

        var provider = new DukascopyDataProvider(downloader);
        var bars = await provider.GetBarsAsync(Request());

        Assert.Single(bars);
        Assert.Equal(2313m, bars[0].Open);
        Assert.Null(provider.LastSpreadStatistics);   // lieber kein Spread als ein erfundener
    }

    [Fact]
    public async Task Records_days_without_data_instead_of_failing()
    {
        // Wochenenden und Feiertage liefern leere Antworten - das ist kein Fehler.
        var provider = new DukascopyDataProvider(new FakeDownloader());

        var bars = await provider.GetBarsAsync(Request(days: 3));

        Assert.Empty(bars);
        Assert.Equal(3, provider.DaysWithoutData.Count);
    }

    [Fact]
    public async Task Builds_the_url_with_a_zero_based_month()
    {
        // Januar ist 00 - der häufigste Fehler beim Ansprechen dieses Feeds.
        var downloader = new FakeDownloader();
        var january = new DateTime(2024, 1, 15, 0, 0, 0, DateTimeKind.Utc);

        await new DukascopyDataProvider(downloader, throttleMilliseconds: 0)
            .GetBarsAsync(new DataRequest(Symbol, Timeframe.M1, january, january.AddDays(1)));

        Assert.Contains("/XAUUSD/2024/00/15/BID_candles_min_1.bi5", downloader.RequestedUrls[0]);
    }

    [Fact]
    public async Task Keeps_only_the_requested_range()
    {
        var downloader = new FakeDownloader();
        downloader.Add("BID_candles_min_1", BuildFile(
            (0, 100000, 100000, 100000, 100000, 1f),
            (3600, 101000, 101000, 101000, 101000, 1f),
            (7200, 102000, 102000, 102000, 102000, 1f)));

        var provider = new DukascopyDataProvider(downloader);
        var bars = await provider.GetBarsAsync(
            new DataRequest(Symbol, Timeframe.M1, Day.AddHours(1), Day.AddHours(2)));

        var bar = Assert.Single(bars);
        Assert.Equal(Day.AddHours(1), bar.OpenTimeUtc);
    }

    [Fact]
    public async Task Caches_files_so_a_second_run_does_not_download_again()
    {
        var directory = Path.Combine(Path.GetTempPath(), "duka-" + Guid.NewGuid().ToString("N"));
        try
        {
            var downloader = new FakeDownloader();
            downloader.Add("BID_candles_min_1", BuildFile((0, 2313000, 2313000, 2313000, 2313000, 1f)));

            await new DukascopyDataProvider(downloader, directory, throttleMilliseconds: 0).GetBarsAsync(Request());
            var afterFirst = downloader.RequestedUrls.Count;
            await new DukascopyDataProvider(downloader, directory, throttleMilliseconds: 0).GetBarsAsync(Request());

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
    public async Task Remembers_that_a_day_had_no_data_instead_of_asking_again()
    {
        // Wochenenden und Feiertage sind rund ein Drittel aller Tage. Würden sie in jedem Lauf
        // erneut angefragt, wäre die Drosselung von Dukascopy garantiert.
        var directory = Path.Combine(Path.GetTempPath(), "duka-" + Guid.NewGuid().ToString("N"));
        try
        {
            var downloader = new FakeDownloader();   // liefert für nichts eine Datei
            var request = Request(days: 2);

            await new DukascopyDataProvider(downloader, directory, throttleMilliseconds: 0).GetBarsAsync(request);
            var afterFirst = downloader.RequestedUrls.Count;
            await new DukascopyDataProvider(downloader, directory, throttleMilliseconds: 0).GetBarsAsync(request);

            Assert.Equal(2, afterFirst);   // zwei Tage, je nur die Bid-Datei
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
    public async Task Does_not_remember_a_missing_day_that_may_simply_be_too_recent()
    {
        // Der Feed hinkt der Gegenwart hinterher. Ein Fehlvermerk für gestern wäre dauerhaft falsch.
        var directory = Path.Combine(Path.GetTempPath(), "duka-" + Guid.NewGuid().ToString("N"));
        try
        {
            var downloader = new FakeDownloader();
            var yesterday = DateTime.UtcNow.Date.AddDays(-1);
            var request = new DataRequest(Symbol, Timeframe.M1, yesterday, yesterday.AddDays(1));

            await new DukascopyDataProvider(downloader, directory, throttleMilliseconds: 0).GetBarsAsync(request);
            await new DukascopyDataProvider(downloader, directory, throttleMilliseconds: 0).GetBarsAsync(request);

            Assert.Equal(2, downloader.RequestedUrls.Count);
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
    public async Task Reports_a_broken_file_instead_of_silently_dropping_it()
    {
        var downloader = new FakeDownloader();
        downloader.Add("BID_candles_min_1", new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14 });

        var error = await Assert.ThrowsAsync<MarketDataException>(
            () => new DukascopyDataProvider(downloader).GetBarsAsync(Request()));

        Assert.Contains("XAUUSD", error.Message);
    }

    [Fact]
    public void Rejects_a_non_positive_price_scale()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new DukascopyDataProvider(new FakeDownloader(), priceScale: 0m));
    }

    /// <summary>Downloader, der für bestimmte Tage eine Störung meldet.</summary>
    private sealed class FailingDownloader : IFileDownloader
    {
        private readonly HashSet<int> _failingDays;
        private readonly byte[] _payload;

        public FailingDownloader(byte[] payload, params int[] failingDays)
        {
            _payload = payload;
            _failingDays = new HashSet<int>(failingDays);
        }

        public Task<byte[]?> TryDownloadAsync(string url, CancellationToken cancellationToken = default)
        {
            foreach (var day in _failingDays)
            {
                if (url.Contains($"/{day:00}/", StringComparison.Ordinal))
                {
                    throw new MarketDataUnavailableException($"Zeitueberschreitung bei '{url}'.");
                }
            }

            return Task.FromResult<byte[]?>(url.Contains("BID", StringComparison.Ordinal) ? _payload : null);
        }
    }

    [Fact]
    public async Task A_single_unreachable_day_does_not_end_a_multi_year_download()
    {
        // Bei rund 500 Dateien je Jahr und Instrument wäre ein Abbruch bei der ersten
        // Zeitüberschreitung gleichbedeutend mit "wird nie fertig".
        var file = BuildFile((0, 2313000, 2313500, 2312800, 2313600, 10f));
        var provider = new DukascopyDataProvider(new FailingDownloader(file, 13), throttleMilliseconds: 0);

        var bars = await provider.GetBarsAsync(Request(days: 3));   // 12., 13., 14. Juni

        Assert.Equal(2, bars.Count);                  // der 12. und der 14.
        var failure = Assert.Single(provider.DaysFailed);
        Assert.Equal(13, failure.DayUtc.Day);
        Assert.Empty(provider.DaysWithoutData);       // ein Fehltag ist kein leerer Tag
    }

    [Fact]
    public async Task A_changed_file_format_still_ends_the_download()
    {
        // Umgekehrter Fall: Stimmt eine Annahme über die Quelle nicht mehr, ist jedes weitere
        // Byte geraten. Das muss laut scheitern und darf nicht als Fehltag durchgehen.
        var downloader = new FakeDownloader();
        downloader.Add("BID_candles_min_1", new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14 });

        var provider = new DukascopyDataProvider(downloader);

        await Assert.ThrowsAsync<MarketDataException>(() => provider.GetBarsAsync(Request(days: 3)));
        Assert.Empty(provider.DaysFailed);
    }

    [Fact]
    public async Task Drops_the_padding_minutes_that_carry_no_volume()
    {
        // Dukascopy füllt jeden Tag auf 1440 Minuten auf. Minuten ohne Tick bekommen den letzten
        // Kurs und Volumen 0. Würden sie übernommen, sähe eine Handelspause aus wie ein ruhiger
        // Markt - und eine Samstagsdatei wie ein voller Handelstag.
        var downloader = new FakeDownloader();
        downloader.Add("BID_candles_min_1", BuildFile(
            (0, 2313000, 2313500, 2312800, 2313600, 120f),
            (60, 2313500, 2313500, 2313500, 2313500, 0f),      // Auffüllung
            (120, 2313500, 2314000, 2313400, 2314200, 95f)));

        var provider = new DukascopyDataProvider(downloader);
        var bars = await provider.GetBarsAsync(Request());

        Assert.Equal(2, bars.Count);
        Assert.Equal(Day, bars[0].OpenTimeUtc);
        Assert.Equal(Day.AddMinutes(2), bars[1].OpenTimeUtc);
        Assert.Equal(1, provider.PaddingMinutesDropped);
    }

    [Fact]
    public async Task Treats_a_file_made_entirely_of_padding_as_a_day_without_data()
    {
        // Genau so sieht eine Samstagsdatei aus: 1440 Sätze, alle ohne Volumen, konstanter Kurs.
        var downloader = new FakeDownloader();
        downloader.Add("BID_candles_min_1", BuildFile(
            (0, 2332184, 2332184, 2332184, 2332184, 0f),
            (60, 2332184, 2332184, 2332184, 2332184, 0f)));

        var provider = new DukascopyDataProvider(downloader);
        var bars = await provider.GetBarsAsync(Request());

        Assert.Empty(bars);
        Assert.Single(provider.DaysWithoutData);
    }

    [Theory]
    [InlineData("XAUUSD", 1000)]      // 2310755 = 2310.755
    [InlineData("XAGUSD", 1000)]      // 29178 = 29.178
    [InlineData("USA500IDXUSD", 1000)]
    [InlineData("USATECHIDXUSD", 1000)]
    [InlineData("EURUSD", 100000)]    // 107346 = 1.07346
    [InlineData("USDJPY", 1000)]      // Yen-Paare haben drei Nachkommastellen
    public void Knows_the_price_scale_of_the_common_instruments(string symbol, int expected)
    {
        // Eine falsche Skalierung fällt nicht von selbst auf: die Kurse sind dann nur um
        // Zehnerpotenzen verschoben und in sich weiterhin stimmig.
        Assert.Equal(expected, DukascopyDataProvider.PriceScaleFor(symbol));
    }
}
