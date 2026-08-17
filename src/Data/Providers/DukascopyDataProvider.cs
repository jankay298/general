using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Daytrading.Strategies.Model;
using SevenZip.Compression.LZMA;

namespace Daytrading.Data.Providers;

/// <summary>
/// Metalle, Indizes und Währungen aus dem historischen Datenfeed von Dukascopy.
/// </summary>
/// <remarks>
/// Die Quelle mit dem besten Verhältnis aus Aufwand und Ertrag für dieses Framework:
/// <list type="bullet">
///   <item><b>Keine Anmeldung, kein Token.</b> Die Dateien liegen offen im Netz.</item>
///   <item><b>Bid und Ask getrennt.</b> Damit wird der historische Spread <i>gemessen</i> statt
///         geschätzt - bei kurzen Haltedauern die Größe, an der alles hängt.</item>
///   <item>Historie über viele Jahre in Minutenauflösung, für XAUUSD, XAGUSD, die großen
///         Indizes und alle Währungspaare.</item>
/// </list>
///
/// Format je Handelstag eine LZMA-gepackte Datei mit 24-Byte-Sätzen, durchgehend big endian:
/// <code>
///   uint32  Sekunden seit 00:00 UTC
///   int32   Open, Close, Low, High   - ganzzahlig, durch die Skalierung zu teilen
///   float32 Volumen
/// </code>
/// Die Kurse sind <b>keine</b> Fließkommazahlen, sondern skalierte Ganzzahlen; als float
/// gelesen ergeben sie 0. Die Reihenfolge ist O/C/L/H, nicht O/H/L/C.
///
/// Nachgemessen an XAUUSD, 12.06.2024, 10:00 UTC: Bid 2313.945, Ask 2314.312 - dieselben Werte
/// wie am Tick-Endpunkt für denselben Zeitpunkt.
///
/// Der Monat in der URL ist zurückgezählt: Januar ist 00, Dezember ist 11.
///
/// Dukascopy drosselt spürbar. Der Provider lädt deshalb bewusst langsam und seriell; ein
/// Jahr eines Instruments sind rund 250 Dateien statt der 8.760, die der Tick-Endpunkt
/// erfordern würde.
/// </remarks>
public sealed class DukascopyDataProvider : IDataProvider
{
    private const string BaseUrl = "https://datafeed.dukascopy.com/datafeed";
    private const int RecordSize = 24;

    /// <summary>So viele Tage zurück gilt ein fehlender Tag als "noch nicht veröffentlicht".</summary>
    private const int MissingDayGraceDays = 3;

    private readonly IFileDownloader _downloader;
    private readonly string? _cacheDirectory;
    private readonly decimal _priceScale;
    private readonly TimeSpan _throttle;

    /// <param name="priceScale">
    /// Teiler für die ganzzahligen Kurse. Er entspricht den Nachkommastellen des Instruments:
    /// 1000 für Metalle, Indizes und JPY-Paare, 100000 für die übrigen Währungspaare. Ein
    /// falscher Wert fällt nicht von selbst auf - die Kurse sind dann nur um Zehnerpotenzen
    /// verschoben und in sich stimmig -, deshalb prüft <see cref="PriceScaleFor"/> ihn anhand
    /// des Symbolnamens und der Aufrufer kann ihn bewusst überschreiben.
    /// </param>
    public DukascopyDataProvider(
        IFileDownloader downloader,
        string? cacheDirectory = null,
        decimal priceScale = 1000m,
        int throttleMilliseconds = 200)
    {
        _downloader = downloader ?? throw new ArgumentNullException(nameof(downloader));
        _cacheDirectory = cacheDirectory;
        _priceScale = priceScale > 0m
            ? priceScale
            : throw new ArgumentOutOfRangeException(nameof(priceScale), priceScale, "Preisskalierung muss positiv sein.");
        _throttle = TimeSpan.FromMilliseconds(Math.Max(0, throttleMilliseconds));

        if (!string.IsNullOrWhiteSpace(_cacheDirectory))
        {
            Directory.CreateDirectory(_cacheDirectory!);
        }
    }

    public string Name => "dukascopy";

    public Timeframe NativeTimeframe => Timeframe.M1;

    /// <summary>
    /// Wird nach jedem Tag gerufen: verarbeitete Tage, Gesamtzahl, gerade geladener Tag.
    /// </summary>
    /// <remarks>
    /// Ein Abruf über mehrere Jahre dauert bei dieser Quelle Stunden. Ohne Rückmeldung ist von
    /// außen nicht zu unterscheiden, ob er arbeitet oder hängt.
    /// </remarks>
    public Action<int, int, DateTime>? Progress { get; set; }

    /// <summary>Aus Bid- und Ask-Kerzen gemessener Spread der letzten Abfrage.</summary>
    public SpreadStatistics? LastSpreadStatistics { get; private set; }

    /// <summary>Tage, für die es keine Daten gab - Wochenenden, Feiertage, Ausfälle.</summary>
    public List<DateTime> DaysWithoutData { get; } = new List<DateTime>();

    /// <summary>Verworfene Auffüll-Minuten ohne Volumen. Gehört in den Datenqualitätsbericht.</summary>
    public int PaddingMinutesDropped { get; private set; }

    /// <summary>
    /// Tage, deren Abruf fehlgeschlagen ist - streng getrennt von <see cref="DaysWithoutData"/>.
    /// </summary>
    /// <remarks>
    /// Ein leerer Tag ist eine Aussage über den Markt, ein fehlgeschlagener eine über die
    /// Leitung. Sie zu vermischen hieße, eine Netzstörung als handelsfreien Tag auszugeben.
    /// Deshalb bricht ein einzelner Fehltag den Lauf nicht ab - bei mehreren hundert Dateien
    /// je Jahr wäre das sonst der Normalfall -, er wird aber einzeln vermerkt und vom Aufrufer
    /// sichtbar gemeldet. Ein erneuter Lauf holt genau diese Tage nach, weil alles Geladene
    /// im Cache liegt.
    /// </remarks>
    public List<DayFailure> DaysFailed { get; } = new List<DayFailure>();

    /// <summary>Ein Tag, dessen Abruf fehlgeschlagen ist.</summary>
    public sealed class DayFailure
    {
        public DayFailure(DateTime dayUtc, string reason)
        {
            DayUtc = dayUtc;
            Reason = reason;
        }

        public DateTime DayUtc { get; }

        public string Reason { get; }
    }

    /// <summary>
    /// Übliche Kursskalierung für ein Dukascopy-Symbol.
    /// </summary>
    /// <remarks>
    /// Nachgemessen am 12.06.2024: XAUUSD 2310755 = 2310.755, XAGUSD 29178 = 29.178,
    /// USA500IDXUSD 5372797 = 5372.797, USATECHIDXUSD 19207643 = 19207.643, EURUSD
    /// 107346 = 1.07346. Metalle, Indizes und Rohstoffe haben drei Nachkommastellen,
    /// Währungspaare fünf - außer denen mit Yen, die ebenfalls drei haben.
    /// </remarks>
    public static decimal PriceScaleFor(string symbol)
    {
        if (string.IsNullOrWhiteSpace(symbol))
        {
            throw new ArgumentException("Symbol darf nicht leer sein.", nameof(symbol));
        }

        var name = symbol.Trim().ToUpperInvariant();

        // Alles, was kein reines Währungspaar ist, kommt mit drei Nachkommastellen.
        var isCurrencyPair = name.Length == 6 && !name.StartsWith("XAU", StringComparison.Ordinal)
                                              && !name.StartsWith("XAG", StringComparison.Ordinal)
                                              && !name.StartsWith("XPT", StringComparison.Ordinal)
                                              && !name.StartsWith("XPD", StringComparison.Ordinal);

        if (!isCurrencyPair || name.Contains("JPY"))
        {
            return 1000m;
        }

        return 100000m;
    }

    public async Task<IReadOnlyList<Candle>> GetBarsAsync(DataRequest request, CancellationToken cancellationToken = default)
    {
        if (request == null)
        {
            throw new ArgumentNullException(nameof(request));
        }

        var bars = new List<Candle>();
        var spreads = new List<decimal>();

        var totalDays = Math.Max(0, (int)Math.Ceiling((request.ToUtc - request.FromUtc.Date).TotalDays));
        var processedDays = 0;

        for (var day = request.FromUtc.Date; day < request.ToUtc; day = day.AddDays(1))
        {
            cancellationToken.ThrowIfCancellationRequested();
            Progress?.Invoke(processedDays++, totalDays, day);

            try
            {
                var bid = await LoadDayAsync(request.Symbol, day, "BID", cancellationToken).ConfigureAwait(false);
                if (bid.Count == 0)
                {
                    DaysWithoutData.Add(day);
                    continue;
                }

                var ask = await LoadDayAsync(request.Symbol, day, "ASK", cancellationToken).ConfigureAwait(false);
                Merge(bid, ask, day, request, bars, spreads);
            }
            catch (MarketDataUnavailableException error)
            {
                // Ein Fehltag beendet den Lauf nicht: Bei rund 500 Dateien je Jahr und Instrument
                // wäre ein Abbruch beim ersten Zeitüberschreiten gleichbedeutend mit "nie fertig".
                // Der Tag wird vermerkt, nicht überspielt - der Aufrufer entscheidet.
                DaysFailed.Add(new DayFailure(day, error.Message));
            }
        }

        LastSpreadStatistics = OandaDataProvider.Summarize(spreads);
        return bars;
    }

    /// <summary>
    /// Führt Bid- und Ask-Kerzen zu Mittelkursen zusammen und sammelt dabei den Spread.
    /// </summary>
    /// <remarks>
    /// Gehandelt wird auf Mittelkursen, die Kosten kommen aus dem Kostenmodell - so bleibt die
    /// Kursreihe frei von der Handelsrichtung. Fehlt die Ask-Datei, wird die Bid-Reihe
    /// verwendet und kein Spread gemeldet; ein geschätzter wäre schlimmer als gar keiner.
    /// </remarks>
    private void Merge(
        IReadOnlyDictionary<DateTime, Ohlc> bid,
        IReadOnlyDictionary<DateTime, Ohlc> ask,
        DateTime day,
        DataRequest request,
        List<Candle> bars,
        List<decimal> spreads)
    {
        foreach (var pair in bid)
        {
            var time = pair.Key;
            if (time < request.FromUtc || time >= request.ToUtc)
            {
                continue;
            }

            var b = pair.Value;
            if (!ask.TryGetValue(time, out var a))
            {
                bars.Add(b.ToCandle(time));
                continue;
            }

            spreads.Add(a.Close - b.Close);
            bars.Add(new Candle(
                time,
                (b.Open + a.Open) / 2m,
                (b.High + a.High) / 2m,
                (b.Low + a.Low) / 2m,
                (b.Close + a.Close) / 2m,
                b.Volume + a.Volume));
        }
    }

    private async Task<IReadOnlyDictionary<DateTime, Ohlc>> LoadDayAsync(
        string symbol, DateTime day, string side, CancellationToken cancellationToken)
    {
        // Der Monat ist in den URLs von Dukascopy null-basiert.
        var name = $"{symbol}-{day:yyyy-MM-dd}-{side}.bi5";
        var url = string.Format(
            CultureInfo.InvariantCulture,
            "{0}/{1}/{2:0000}/{3:00}/{4:00}/{5}_candles_min_1.bi5",
            BaseUrl, symbol, day.Year, day.Month - 1, day.Day, side);

        var payload = await LoadAsync(url, name, day, cancellationToken).ConfigureAwait(false);
        return payload == null || payload.Length == 0
            ? new Dictionary<DateTime, Ohlc>()
            : Decode(payload, day, url);
    }

    /// <summary>
    /// Holt eine Datei aus dem Cache oder vom Server.
    /// </summary>
    /// <remarks>
    /// Auch <i>fehlende</i> Tage werden gespeichert - als leere Datei. Wochenenden und Feiertage
    /// sind rund ein Drittel aller Kalendertage; ohne diesen Vermerk würde jeder Lauf sie erneut
    /// anfragen und damit genau die Drosselung provozieren, die es zu vermeiden gilt.
    ///
    /// Ausgenommen sind die letzten Tage: Dass eine Datei heute fehlt, heißt nicht, dass sie
    /// morgen fehlt - der Feed hinkt der Gegenwart hinterher. Ein Fehlvermerk für gestern wäre
    /// dauerhaft falsch.
    /// </remarks>
    private async Task<byte[]?> LoadAsync(string url, string fileName, DateTime day, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_cacheDirectory))
        {
            return await Fetch(url, cancellationToken).ConfigureAwait(false);
        }

        var path = Path.Combine(_cacheDirectory!, fileName);
        if (File.Exists(path))
        {
            return File.ReadAllBytes(path);
        }

        var fresh = await Fetch(url, cancellationToken).ConfigureAwait(false);

        if (fresh != null)
        {
            File.WriteAllBytes(path, fresh);
        }
        else if (day.Date < DateTime.UtcNow.Date.AddDays(-MissingDayGraceDays))
        {
            File.WriteAllBytes(path, Array.Empty<byte>());
        }

        return fresh;
    }

    private async Task<byte[]?> Fetch(string url, CancellationToken cancellationToken)
    {
        var payload = await _downloader.TryDownloadAsync(url, cancellationToken).ConfigureAwait(false);

        if (_throttle > TimeSpan.Zero)
        {
            // Freiwillige Pause. Dukascopy stellt die Daten kostenlos bereit; sie im
            // Sekundentakt zu bombardieren wäre der schnellste Weg, gesperrt zu werden.
            await Task.Delay(_throttle, cancellationToken).ConfigureAwait(false);
        }

        return payload;
    }

    private Dictionary<DateTime, Ohlc> Decode(byte[] payload, DateTime day, string url)
    {
        byte[] data;
        try
        {
            data = Decompress(payload);
        }
        catch (Exception error)
        {
            throw new MarketDataException($"'{url}' liess sich nicht entpacken: {error.Message}", error);
        }

        if (data.Length % RecordSize != 0)
        {
            throw new MarketDataException(
                $"'{url}' hat {data.Length} Bytes, kein Vielfaches der Satzlänge {RecordSize}. Format geändert?");
        }

        var result = new Dictionary<DateTime, Ohlc>(data.Length / RecordSize);
        var padding = 0;

        for (var offset = 0; offset + RecordSize <= data.Length; offset += RecordSize)
        {
            var seconds = ReadUInt32(data, offset);
            var open = ReadInt32(data, offset + 4);
            var close = ReadInt32(data, offset + 8);
            var low = ReadInt32(data, offset + 12);
            var high = ReadInt32(data, offset + 16);
            var volume = ReadSingle(data, offset + 20);

            // Dukascopy liefert jeden Tag mit allen 1440 Minuten aus. Minuten ohne einen einzigen
            // Tick werden mit dem letzten Kurs und Volumen 0 aufgefüllt. Diese Sätze sind keine
            // Marktdaten, sondern Platzhalter - eine Samstagsdatei besteht ausschließlich daraus.
            // Sie zu übernehmen hieße, eine Handelspause als ruhigen Markt auszugeben.
            if (volume <= 0f)
            {
                padding++;
                continue;
            }

            var time = DateTime.SpecifyKind(day.Date, DateTimeKind.Utc).AddSeconds(seconds);
            result[time] = new Ohlc(
                Scale(open), Scale(high), Scale(low), Scale(close), (decimal)volume);
        }

        PaddingMinutesDropped += padding;
        return result;
    }

    private decimal Scale(int value) => value / _priceScale;

    private static byte[] Decompress(byte[] payload)
    {
        // LZMA im "alone"-Format: fünf Byte Eigenschaften, acht Byte Ausgabegröße, dann die Daten.
        if (payload.Length < 13)
        {
            return Array.Empty<byte>();
        }

        var properties = new byte[5];
        Array.Copy(payload, 0, properties, 0, 5);
        var outputSize = BitConverter.ToInt64(payload, 5);

        if (outputSize <= 0 || outputSize > 64 * 1024 * 1024)
        {
            throw new MarketDataException($"Unplausible Ausgabegröße {outputSize} im LZMA-Kopf.");
        }

        using var input = new MemoryStream(payload, 13, payload.Length - 13);
        using var output = new MemoryStream((int)outputSize);

        var decoder = new Decoder();
        decoder.SetDecoderProperties(properties);
        decoder.Code(input, output, payload.Length - 13, outputSize, null);

        return output.ToArray();
    }

    private static uint ReadUInt32(byte[] data, int offset) =>
        ((uint)data[offset] << 24) | ((uint)data[offset + 1] << 16) | ((uint)data[offset + 2] << 8) | data[offset + 3];

    private static int ReadInt32(byte[] data, int offset) => (int)ReadUInt32(data, offset);

    private static float ReadSingle(byte[] data, int offset)
    {
        var bytes = new[] { data[offset + 3], data[offset + 2], data[offset + 1], data[offset] };
        return BitConverter.ToSingle(bytes, 0);
    }

    private readonly struct Ohlc
    {
        public Ohlc(decimal open, decimal high, decimal low, decimal close, decimal volume)
        {
            Open = open;
            High = high;
            Low = low;
            Close = close;
            Volume = volume;
        }

        public decimal Open { get; }

        public decimal High { get; }

        public decimal Low { get; }

        public decimal Close { get; }

        public decimal Volume { get; }

        public Candle ToCandle(DateTime timeUtc) => new Candle(timeUtc, Open, High, Low, Close, Volume);
    }
}
