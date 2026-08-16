using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Daytrading.Strategies.Model;

namespace Daytrading.Data.Config;

/// <summary>
/// Konfiguration eines handelbaren Symbols. Kommt aus einer Datei, nie aus dem Code -
/// ein neues Instrument darf keine Codeänderung erfordern.
/// </summary>
public sealed class SymbolConfig
{
    /// <summary>Interner Name, unter dem das Symbol in Ergebnissen und Logs erscheint.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Name bei der Datenquelle, falls abweichend (z.B. BTCUSDT bei Binance).</summary>
    public string? SourceSymbol { get; set; }

    public AssetClass AssetClass { get; set; }

    /// <summary>IANA-Zeitzone der Börse, z.B. "America/New_York". Für Krypto irrelevant.</summary>
    public string TimeZone { get; set; } = "UTC";

    /// <summary>Sessionbeginn in Ortszeit der Börse, z.B. "09:30".</summary>
    public string SessionStart { get; set; } = "00:00";

    /// <summary>Sessionende in Ortszeit der Börse, z.B. "16:00". "24:00" bedeutet Tagesende.</summary>
    public string SessionEnd { get; set; } = "24:00";

    /// <summary>Handelstage. Leer bedeutet Montag bis Freitag; für Krypto alle sieben Tage.</summary>
    public List<string> TradingDays { get; set; } = new List<string>();

    /// <summary>Feiertagskalender: "us-equity" oder "none".</summary>
    public string HolidayCalendar { get; set; } = "none";

    /// <summary>Arbeitsauflösung für den Backtest. Gespeichert wird immer feiner.</summary>
    public string WorkingTimeframe { get; set; } = "M15";

    public decimal TickSize { get; set; } = 0.01m;

    public decimal MinQuantity { get; set; } = 1m;

    public decimal QuantityStep { get; set; } = 1m;

    public decimal MaxQuantity { get; set; } = 1_000_000m;

    /// <summary>Wert einer Preisbewegung von 1.0 je Einheit in Kontowährung.</summary>
    public decimal ValuePerPricePointPerUnit { get; set; } = 1m;

    /// <summary>Typischer Spread in Preiseinheiten. Aus den Brokerdaten, nicht geschätzt.</summary>
    public decimal TypicalSpread { get; set; }

    /// <summary>Kommission je Einheit und Handelsrichtung in Kontowährung.</summary>
    public decimal CommissionPerUnitPerSide { get; set; }

    /// <summary>Zuschlag auf den Spread in illiquiden Phasen, als Faktor. 1.0 bedeutet kein Zuschlag.</summary>
    public decimal ThinLiquiditySpreadFactor { get; set; } = 1m;

    public SymbolInfo ToSymbolInfo() => new SymbolInfo(Name, AssetClass, TickSize);

    public Timeframe ToTimeframe() => ConfigParser.ParseTimeframe(WorkingTimeframe, Name);

    public string SourceName => string.IsNullOrWhiteSpace(SourceSymbol) ? Name : SourceSymbol!;

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Name))
        {
            throw new MarketDataException("Symbolkonfiguration ohne Namen.");
        }

        if (TickSize <= 0m)
        {
            throw new MarketDataException($"{Name}: TickSize muss positiv sein.");
        }

        if (MinQuantity <= 0m || QuantityStep <= 0m || MaxQuantity < MinQuantity)
        {
            throw new MarketDataException($"{Name}: Mengenangaben sind widersprüchlich.");
        }

        if (ValuePerPricePointPerUnit <= 0m)
        {
            throw new MarketDataException($"{Name}: ValuePerPricePointPerUnit muss positiv sein.");
        }

        if (TypicalSpread < 0m || CommissionPerUnitPerSide < 0m)
        {
            throw new MarketDataException($"{Name}: Kosten dürfen nicht negativ sein.");
        }

        if (ThinLiquiditySpreadFactor < 1m)
        {
            throw new MarketDataException(
                $"{Name}: ThinLiquiditySpreadFactor unter 1 würde dünne Phasen billiger machen als normale.");
        }

        ConfigParser.ParseTimeframe(WorkingTimeframe, Name);
        ConfigParser.ParseTimeOfDay(SessionStart, Name);
        ConfigParser.ParseTimeOfDay(SessionEnd, Name);
        ConfigParser.ResolveTimeZone(TimeZone, Name);
        ConfigParser.ParseTradingDays(TradingDays, AssetClass, Name);
    }
}

/// <summary>Alle konfigurierten Symbole.</summary>
public sealed class SymbolCatalog
{
    public SymbolCatalog(IEnumerable<SymbolConfig> symbols)
    {
        Symbols = symbols?.ToList() ?? throw new ArgumentNullException(nameof(symbols));

        foreach (var symbol in Symbols)
        {
            symbol.Validate();
        }

        var duplicates = Symbols
            .GroupBy(symbol => symbol.Name, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToList();

        if (duplicates.Count > 0)
        {
            throw new MarketDataException($"Symbole doppelt konfiguriert: {string.Join(", ", duplicates)}.");
        }
    }

    public IReadOnlyList<SymbolConfig> Symbols { get; }

    public SymbolConfig this[string name] =>
        Symbols.FirstOrDefault(symbol => string.Equals(symbol.Name, name, StringComparison.OrdinalIgnoreCase))
        ?? throw new MarketDataException($"Symbol '{name}' ist nicht konfiguriert.");

    public static SymbolCatalog Load(string path)
    {
        if (!System.IO.File.Exists(path))
        {
            throw new MarketDataException($"Symbolkonfiguration '{path}' wurde nicht gefunden.");
        }

        return Parse(System.IO.File.ReadAllText(path));
    }

    public static SymbolCatalog Parse(string json)
    {
        var options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
            Converters = { new JsonStringEnumConverter(allowIntegerValues: false) },
        };

        List<SymbolConfig>? symbols;
        try
        {
            symbols = JsonSerializer.Deserialize<List<SymbolConfig>>(json, options);
        }
        catch (JsonException error)
        {
            throw new MarketDataException($"Symbolkonfiguration ist kein gültiges JSON: {error.Message}", error);
        }

        if (symbols == null || symbols.Count == 0)
        {
            throw new MarketDataException("Symbolkonfiguration enthält kein einziges Symbol.");
        }

        return new SymbolCatalog(symbols);
    }
}

/// <summary>Gemeinsame Parser für Konfigurationswerte, damit Fehlermeldungen überall gleich klingen.</summary>
internal static class ConfigParser
{
    public static Timeframe ParseTimeframe(string text, string symbolName)
    {
        if (!Timeframe.TryParse(text, out var timeframe))
        {
            throw new MarketDataException($"{symbolName}: '{text}' ist kein gültiger Timeframe.");
        }

        return timeframe;
    }

    public static TimeSpan ParseTimeOfDay(string text, string symbolName)
    {
        if (string.Equals(text, "24:00", StringComparison.Ordinal))
        {
            return TimeSpan.FromHours(24);
        }

        if (!TimeSpan.TryParse(text, System.Globalization.CultureInfo.InvariantCulture, out var value)
            || value < TimeSpan.Zero
            || value > TimeSpan.FromHours(24))
        {
            throw new MarketDataException($"{symbolName}: '{text}' ist keine gültige Uhrzeit (erwartet HH:mm).");
        }

        return value;
    }

    public static TimeZoneInfo ResolveTimeZone(string id, string symbolName)
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(id);
        }
        catch (Exception error)
        {
            throw new MarketDataException(
                $"{symbolName}: Zeitzone '{id}' ist unbekannt. Erwartet wird eine IANA-Kennung wie 'America/New_York'.",
                error);
        }
    }

    public static IReadOnlyList<DayOfWeek> ParseTradingDays(List<string> days, AssetClass assetClass, string symbolName)
    {
        if (days == null || days.Count == 0)
        {
            return assetClass == AssetClass.Crypto
                ? new[]
                {
                    DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday,
                    DayOfWeek.Friday, DayOfWeek.Saturday, DayOfWeek.Sunday,
                }
                : new[]
                {
                    DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday,
                };
        }

        var result = new List<DayOfWeek>();
        foreach (var day in days)
        {
            if (!Enum.TryParse<DayOfWeek>(day, ignoreCase: true, out var parsed))
            {
                throw new MarketDataException($"{symbolName}: '{day}' ist kein Wochentag.");
            }

            result.Add(parsed);
        }

        return result;
    }
}
