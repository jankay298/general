using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Threading;
using System.Threading.Tasks;
using Daytrading.Data.Normalization;
using Daytrading.Strategies.Model;

namespace Daytrading.Data.Providers;

/// <summary>
/// Kryptodaten aus dem öffentlichen Archiv von Binance (data.binance.vision).
/// </summary>
/// <remarks>
/// Kostenlos, ohne Zugangsdaten, mit vollständiger Historie in 1-Minuten-Auflösung. Die Daten
/// liegen als Monats-ZIPs vor; für den laufenden Monat gibt es Tagesarchive. Beides wird lokal
/// zwischengespeichert, damit ein zweiter Backtest-Lauf nicht erneut lädt.
///
/// Wichtig: Binance-Kurse sind Börsenkurse einer Kryptobörse, nicht die des cTrader-Brokers.
/// Sie taugen zur Strategieforschung; die Kostenannahmen für den Livebetrieb kommen aus den
/// Brokerdaten.
/// </remarks>
public sealed class BinancePublicDataProvider : IDataProvider
{
    private const string BaseUrl = "https://data.binance.vision/data/spot";

    private readonly IFileDownloader _downloader;
    private readonly string? _cacheDirectory;

    public BinancePublicDataProvider(IFileDownloader downloader, string? cacheDirectory = null)
    {
        _downloader = downloader ?? throw new ArgumentNullException(nameof(downloader));
        _cacheDirectory = cacheDirectory;

        if (!string.IsNullOrWhiteSpace(_cacheDirectory))
        {
            Directory.CreateDirectory(_cacheDirectory!);
        }
    }

    public string Name => "binance-public-data";

    public Timeframe NativeTimeframe => Timeframe.M1;

    /// <summary>Monatsarchive, die es (noch) nicht gibt. Für den Qualitätsreport interessant.</summary>
    public List<string> MissingArchives { get; } = new List<string>();

    public async Task<IReadOnlyList<Candle>> GetBarsAsync(DataRequest request, CancellationToken cancellationToken = default)
    {
        if (request == null)
        {
            throw new ArgumentNullException(nameof(request));
        }

        var interval = ToBinanceInterval(request.Timeframe);
        var bars = new List<Candle>();

        var month = new DateTime(request.FromUtc.Year, request.FromUtc.Month, 1, 0, 0, 0, DateTimeKind.Utc);

        // Das Ende des Zeitraums ist exklusiv: Ein Zeitraum bis zum 1. Februar 00:00 braucht
        // kein Februar-Archiv. Sonst würde jeder Lauf eine Datei anfragen, die er nie benutzt.
        var lastInstant = request.ToUtc.AddTicks(-1);
        var lastMonth = new DateTime(lastInstant.Year, lastInstant.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        var currentMonth = new DateTime(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1, 0, 0, 0, DateTimeKind.Utc);

        while (month <= lastMonth)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (month < currentMonth)
            {
                var name = $"{request.Symbol}-{interval}-{month:yyyy-MM}.zip";
                var url = $"{BaseUrl}/monthly/klines/{request.Symbol}/{interval}/{name}";
                var payload = await LoadAsync(url, name, cancellationToken).ConfigureAwait(false);

                if (payload == null)
                {
                    MissingArchives.Add(name);
                }
                else
                {
                    ReadZip(payload, bars);
                }
            }
            else
            {
                // Laufender Monat: Tagesarchive, weil das Monatsarchiv erst nach Monatsende entsteht.
                var day = month;
                var lastDay = month.AddMonths(1);
                while (day < lastDay && day < request.ToUtc)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var name = $"{request.Symbol}-{interval}-{day:yyyy-MM-dd}.zip";
                    var url = $"{BaseUrl}/daily/klines/{request.Symbol}/{interval}/{name}";
                    var payload = await LoadAsync(url, name, cancellationToken).ConfigureAwait(false);

                    if (payload != null)
                    {
                        ReadZip(payload, bars);
                    }

                    day = day.AddDays(1);
                }
            }

            month = month.AddMonths(1);
        }

        var normalized = BarNormalizer.Normalize(bars, out _);
        var result = new List<Candle>(normalized.Count);
        foreach (var bar in normalized)
        {
            if (bar.OpenTimeUtc >= request.FromUtc && bar.OpenTimeUtc < request.ToUtc)
            {
                result.Add(bar);
            }
        }

        return result;
    }

    private async Task<byte[]?> LoadAsync(string url, string fileName, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_cacheDirectory))
        {
            return await _downloader.TryDownloadAsync(url, cancellationToken).ConfigureAwait(false);
        }

        var path = Path.Combine(_cacheDirectory!, fileName);
        if (File.Exists(path))
        {
            return File.ReadAllBytes(path);
        }

        var payload = await _downloader.TryDownloadAsync(url, cancellationToken).ConfigureAwait(false);
        if (payload != null)
        {
            File.WriteAllBytes(path, payload);
        }

        return payload;
    }

    private static void ReadZip(byte[] payload, List<Candle> target)
    {
        using var stream = new MemoryStream(payload);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);

        foreach (var entry in archive.Entries)
        {
            if (!entry.Name.EndsWith(".csv", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            using var entryStream = entry.Open();
            using var reader = new StreamReader(entryStream);
            KlineCsvParser.Parse(reader, target);
        }
    }

    private static string ToBinanceInterval(Timeframe timeframe)
    {
        var minutes = timeframe.Duration.TotalMinutes;
        if (minutes < 1 || minutes % 1 != 0)
        {
            throw new MarketDataException($"Binance liefert keine Auflösung von {timeframe}.");
        }

        return minutes switch
        {
            1 => "1m",
            3 => "3m",
            5 => "5m",
            15 => "15m",
            30 => "30m",
            60 => "1h",
            120 => "2h",
            240 => "4h",
            360 => "6h",
            480 => "8h",
            720 => "12h",
            1440 => "1d",
            _ => throw new MarketDataException(
                $"Binance kennt keine Auflösung {timeframe}. Feiner laden und im Code aggregieren."),
        };
    }
}

/// <summary>
/// Parser für Binance-Klines im CSV-Format.
/// </summary>
/// <remarks>
/// Zwei Eigenheiten des Formats, die ohne Behandlung stille Fehler erzeugen:
/// neuere Archive enthalten eine Kopfzeile, und die Zeitstempel liegen je nach Jahrgang in
/// Sekunden, Millisekunden oder Mikrosekunden vor. Beides wird an der Größenordnung erkannt.
/// </remarks>
public static class KlineCsvParser
{
    public static void Parse(TextReader reader, List<Candle> target)
    {
        if (reader == null)
        {
            throw new ArgumentNullException(nameof(reader));
        }

        string? line;
        var lineNumber = 0;

        while ((line = reader.ReadLine()) != null)
        {
            lineNumber++;
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var fields = line.Split(',');
            if (fields.Length < 6)
            {
                throw new MarketDataException($"Zeile {lineNumber} hat nur {fields.Length} Spalten: '{line}'.");
            }

            if (!long.TryParse(fields[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var rawTime))
            {
                if (lineNumber == 1)
                {
                    continue;   // Kopfzeile
                }

                throw new MarketDataException($"Zeile {lineNumber}: '{fields[0]}' ist kein Zeitstempel.");
            }

            var openTime = FromEpoch(rawTime);
            var open = ParseNumber(fields[1], lineNumber, "open");
            var high = ParseNumber(fields[2], lineNumber, "high");
            var low = ParseNumber(fields[3], lineNumber, "low");
            var close = ParseNumber(fields[4], lineNumber, "close");
            var volume = ParseNumber(fields[5], lineNumber, "volume");

            target.Add(new Candle(openTime, open, high, low, close, volume));
        }
    }

    /// <summary>Erkennt an der Größenordnung, ob Sekunden, Millisekunden oder Mikrosekunden vorliegen.</summary>
    public static DateTime FromEpoch(long value)
    {
        if (value >= 100_000_000_000_000L)
        {
            return DateTimeOffset.FromUnixTimeMilliseconds(value / 1000).UtcDateTime;
        }

        if (value >= 100_000_000_000L)
        {
            return DateTimeOffset.FromUnixTimeMilliseconds(value).UtcDateTime;
        }

        return DateTimeOffset.FromUnixTimeSeconds(value).UtcDateTime;
    }

    private static decimal ParseNumber(string text, int lineNumber, string column)
    {
        if (!decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
        {
            throw new MarketDataException($"Zeile {lineNumber}, Spalte {column}: '{text}' ist keine Zahl.");
        }

        return value;
    }
}
