using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Daytrading.Strategies.Model;
using Parquet.Serialization;

namespace Daytrading.Data.Storage;

/// <summary>Ablage normalisierter Kursdaten.</summary>
public interface IBarStore
{
    Task WriteAsync(string symbol, Timeframe timeframe, IReadOnlyList<Candle> bars, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<Candle>> ReadAsync(
        string symbol, Timeframe timeframe, DateTime fromUtc, DateTime toUtc, CancellationToken cancellationToken = default);

    bool Contains(string symbol, Timeframe timeframe);
}

/// <summary>Eine Zeile in der Parquet-Datei.</summary>
public sealed class BarRow
{
    public DateTime OpenTimeUtc { get; set; }

    public decimal Open { get; set; }

    public decimal High { get; set; }

    public decimal Low { get; set; }

    public decimal Close { get; set; }

    public decimal Volume { get; set; }
}

/// <summary>
/// Kursdaten als Parquet, partitioniert nach Symbol, Auflösung und Jahr.
/// </summary>
/// <remarks>
/// Pfad: <c>{root}/{symbol}/{timeframe}/{jahr}.parquet</c>. Die Partitionierung nach Jahr hält
/// die Dateien handhabbar und erlaubt es, einen Zeitraum zu lesen, ohne die gesamte Historie
/// zu laden. CSV bleibt Austauschformat, nicht Speicherformat: bei mehreren Jahren
/// Minutendaten je Symbol ist der Unterschied in Größe und Ladezeit erheblich.
///
/// Beim Lesen wird der Zeitstempel wieder ausdrücklich als UTC gekennzeichnet - Parquet
/// speichert keine Zeitzone, und eine Bar ohne Zeitzonenkennung würde beim Anlegen eines
/// <see cref="Candle"/> zu Recht abgelehnt.
/// </remarks>
public sealed class ParquetBarStore : IBarStore
{
    private readonly string _root;

    public ParquetBarStore(string rootDirectory)
    {
        if (string.IsNullOrWhiteSpace(rootDirectory))
        {
            throw new ArgumentException("Wurzelverzeichnis darf nicht leer sein.", nameof(rootDirectory));
        }

        _root = rootDirectory;
    }

    public bool Contains(string symbol, Timeframe timeframe) =>
        Directory.Exists(DirectoryFor(symbol, timeframe))
        && Directory.EnumerateFiles(DirectoryFor(symbol, timeframe), "*.parquet").Any();

    public async Task WriteAsync(
        string symbol,
        Timeframe timeframe,
        IReadOnlyList<Candle> bars,
        CancellationToken cancellationToken = default)
    {
        if (bars == null)
        {
            throw new ArgumentNullException(nameof(bars));
        }

        var directory = DirectoryFor(symbol, timeframe);
        Directory.CreateDirectory(directory);

        foreach (var year in bars.GroupBy(bar => bar.OpenTimeUtc.Year).OrderBy(group => group.Key))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var rows = year
                .OrderBy(bar => bar.OpenTimeUtc)
                .Select(bar => new BarRow
                {
                    OpenTimeUtc = bar.OpenTimeUtc,
                    Open = bar.Open,
                    High = bar.High,
                    Low = bar.Low,
                    Close = bar.Close,
                    Volume = bar.Volume,
                })
                .ToList();

            var path = Path.Combine(directory, $"{year.Key}.parquet");
            using var stream = File.Create(path);
            await ParquetSerializer.SerializeAsync(rows, stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task<IReadOnlyList<Candle>> ReadAsync(
        string symbol,
        Timeframe timeframe,
        DateTime fromUtc,
        DateTime toUtc,
        CancellationToken cancellationToken = default)
    {
        var directory = DirectoryFor(symbol, timeframe);
        if (!Directory.Exists(directory))
        {
            throw new MarketDataException(
                $"Für {symbol} ({timeframe}) liegen keine Daten unter '{directory}'. Erst die Pipeline laufen lassen.");
        }

        var result = new List<Candle>();

        for (var year = fromUtc.Year; year <= toUtc.Year; year++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var path = Path.Combine(directory, $"{year}.parquet");
            if (!File.Exists(path))
            {
                continue;
            }

            using var stream = File.OpenRead(path);
            var rows = await ParquetSerializer.DeserializeAsync<BarRow>(stream, cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            foreach (var row in rows.Data)
            {
                var time = DateTime.SpecifyKind(row.OpenTimeUtc, DateTimeKind.Utc);
                if (time < fromUtc || time >= toUtc)
                {
                    continue;
                }

                result.Add(new Candle(time, row.Open, row.High, row.Low, row.Close, row.Volume));
            }
        }

        result.Sort((left, right) => left.OpenTimeUtc.CompareTo(right.OpenTimeUtc));
        return result;
    }

    private string DirectoryFor(string symbol, Timeframe timeframe) =>
        Path.Combine(_root, Sanitize(symbol), timeframe.ToString());

    private static string Sanitize(string name)
    {
        foreach (var invalid in Path.GetInvalidFileNameChars())
        {
            name = name.Replace(invalid, '_');
        }

        return name;
    }
}
