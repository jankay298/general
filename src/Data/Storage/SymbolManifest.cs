using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Daytrading.Data.Storage;

/// <summary>
/// Herkunftsnachweis je Symbol: Woher stammen die Daten, welcher Zeitraum, welche Auflösung,
/// wann geladen, welche Qualität.
/// </summary>
/// <remarks>
/// Ohne Manifest lässt sich Monate später nicht mehr sagen, auf welcher Datenbasis ein Ergebnis
/// entstanden ist - und ein Backtest, dessen Datenherkunft unbekannt ist, ist wertlos.
/// </remarks>
public sealed class SymbolManifest
{
    private static readonly JsonSerializerOptions Options = new JsonSerializerOptions
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public string Symbol { get; set; } = string.Empty;

    public string SourceSymbol { get; set; } = string.Empty;

    public string Source { get; set; } = string.Empty;

    public string StoredTimeframe { get; set; } = string.Empty;

    public string WorkingTimeframe { get; set; } = string.Empty;

    public DateTime FromUtc { get; set; }

    public DateTime ToUtc { get; set; }

    public DateTime DownloadedAtUtc { get; set; }

    public int BarCount { get; set; }

    public string QualityStatus { get; set; } = string.Empty;

    public decimal MissingPercent { get; set; }

    /// <summary>Gemessener Spread, wenn die Quelle Bid und Ask liefert. Sonst null - dann gilt die Konfiguration.</summary>
    public decimal? MeasuredMedianSpread { get; set; }

    /// <summary>Bei Aktien entscheidend: bereinigt oder unbereinigt. Unbereinigte Daten erzeugen Fake-Gaps.</summary>
    public string Adjustment { get; set; } = "unbekannt";

    public string Notes { get; set; } = string.Empty;

    public void Save(string path)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(path, JsonSerializer.Serialize(this, Options));
    }

    public static SymbolManifest Load(string path)
    {
        if (!File.Exists(path))
        {
            throw new MarketDataException($"Manifest '{path}' wurde nicht gefunden.");
        }

        return JsonSerializer.Deserialize<SymbolManifest>(File.ReadAllText(path), Options)
               ?? throw new MarketDataException($"Manifest '{path}' ist leer.");
    }
}
