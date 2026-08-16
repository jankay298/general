using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Daytrading.Data.Normalization;
using Daytrading.Strategies.Model;

namespace Daytrading.Data.Providers;

/// <summary>
/// Liest Kursdaten aus lokalen CSV-Dateien - insbesondere den Export aus cTrader.
/// </summary>
/// <remarks>
/// Das ist die <b>Referenzquelle</b>: Nur die Daten des eigenen Brokers enthalten dessen
/// Preisstellung und Spread. Externe Quellen liefern historische Tiefe und werden hiergegen
/// plausibilisiert.
///
/// Das Format ist absichtlich nachsichtig: Komma oder Semikolon als Trenner, Zeitstempel als
/// ISO-Text oder Epoch-Zahl, optionale Kopfzeile, optionale Spread-Spalte. Ein Exportformat,
/// an dem der Import scheitert, hilft niemandem.
/// </remarks>
public sealed class CsvFileDataProvider : IDataProvider
{
    private readonly string _directory;
    private readonly Timeframe _timeframe;

    public CsvFileDataProvider(string directory, Timeframe timeframe)
    {
        if (string.IsNullOrWhiteSpace(directory))
        {
            throw new ArgumentException("Verzeichnis darf nicht leer sein.", nameof(directory));
        }

        _directory = directory;
        _timeframe = timeframe;
    }

    public string Name => "csv";

    public Timeframe NativeTimeframe => _timeframe;

    public Task<IReadOnlyList<Candle>> GetBarsAsync(DataRequest request, CancellationToken cancellationToken = default)
    {
        if (request == null)
        {
            throw new ArgumentNullException(nameof(request));
        }

        var candidates = new[]
        {
            Path.Combine(_directory, $"{request.Symbol}-{request.Timeframe}.csv"),
            Path.Combine(_directory, $"{request.Symbol}_{request.Timeframe}.csv"),
            Path.Combine(_directory, $"{request.Symbol}.csv"),
        };

        foreach (var path in candidates)
        {
            if (!File.Exists(path))
            {
                continue;
            }

            var bars = ReadFile(path);
            var normalized = BarNormalizer.Normalize(bars, out _);
            var result = new List<Candle>(normalized.Count);
            foreach (var bar in normalized)
            {
                if (bar.OpenTimeUtc >= request.FromUtc && bar.OpenTimeUtc < request.ToUtc)
                {
                    result.Add(bar);
                }
            }

            return Task.FromResult<IReadOnlyList<Candle>>(result);
        }

        throw new MarketDataException(
            $"Keine CSV-Datei für '{request.Symbol}' in '{_directory}' gefunden. Gesucht: {string.Join(", ", candidates)}");
    }

    /// <summary>Liest eine einzelne Datei. Öffentlich, damit Exporte auch außerhalb der Pipeline prüfbar sind.</summary>
    public static List<Candle> ReadFile(string path)
    {
        var bars = new List<Candle>();
        using var reader = new StreamReader(path);

        string? line;
        var lineNumber = 0;
        char? separator = null;

        while ((line = reader.ReadLine()) != null)
        {
            lineNumber++;
            if (string.IsNullOrWhiteSpace(line) || line.StartsWith("#", StringComparison.Ordinal))
            {
                continue;
            }

            separator ??= line.Contains(';') ? ';' : ',';
            var fields = line.Split(separator.Value);

            if (fields.Length < 5)
            {
                throw new MarketDataException(
                    $"{Path.GetFileName(path)}, Zeile {lineNumber}: nur {fields.Length} Spalten. " +
                    "Erwartet werden Zeit, Open, High, Low, Close und optional Volumen.");
            }

            if (!TryParseTimestamp(fields[0], out var time))
            {
                if (lineNumber == 1)
                {
                    continue;   // Kopfzeile
                }

                throw new MarketDataException(
                    $"{Path.GetFileName(path)}, Zeile {lineNumber}: '{fields[0]}' ist kein Zeitstempel.");
            }

            var open = ParseNumber(fields[1], path, lineNumber);
            var high = ParseNumber(fields[2], path, lineNumber);
            var low = ParseNumber(fields[3], path, lineNumber);
            var close = ParseNumber(fields[4], path, lineNumber);
            var volume = fields.Length > 5 && !string.IsNullOrWhiteSpace(fields[5])
                ? ParseNumber(fields[5], path, lineNumber)
                : 0m;

            bars.Add(new Candle(time, open, high, low, close, volume));
        }

        return bars;
    }

    private static bool TryParseTimestamp(string text, out DateTime timeUtc)
    {
        var value = text.Trim().Trim('"');

        if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var epoch) && epoch > 100_000_000L)
        {
            timeUtc = KlineCsvParser.FromEpoch(epoch);
            return true;
        }

        if (DateTime.TryParse(
                value,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal,
                out var parsed))
        {
            timeUtc = DateTime.SpecifyKind(parsed, DateTimeKind.Utc);
            return true;
        }

        timeUtc = default;
        return false;
    }

    private static decimal ParseNumber(string text, string path, int lineNumber)
    {
        if (!decimal.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
        {
            throw new MarketDataException(
                $"{Path.GetFileName(path)}, Zeile {lineNumber}: '{text}' ist keine Zahl. " +
                "Dezimaltrennzeichen ist der Punkt.");
        }

        return value;
    }
}
