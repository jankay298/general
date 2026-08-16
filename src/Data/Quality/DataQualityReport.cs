using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Daytrading.Strategies.Model;

namespace Daytrading.Data.Quality;

public enum DataQualityStatus
{
    /// <summary>Vollständig genug für einen Backtest.</summary>
    Good = 0,

    /// <summary>Nutzbar, aber mit benannten Einschränkungen im Report.</summary>
    Warning = 1,

    /// <summary>Fliegt aus dem Backtest. Ergebnisse auf dieser Datenbasis wären wertlos.</summary>
    Rejected = 2,
}

/// <summary>Eine zusammenhängende Folge fehlender Bars.</summary>
public sealed class DataGap
{
    public DataGap(DateTime fromUtc, DateTime toUtc, int missingBars)
    {
        FromUtc = fromUtc;
        ToUtc = toUtc;
        MissingBars = missingBars;
    }

    public DateTime FromUtc { get; }

    public DateTime ToUtc { get; }

    public int MissingBars { get; }

    public override string ToString() =>
        string.Format(
            CultureInfo.InvariantCulture,
            "{0:yyyy-MM-dd HH:mm} - {1:yyyy-MM-dd HH:mm} ({2} Bars)", FromUtc, ToUtc, MissingBars);
}

/// <summary>
/// Was an den Daten eines Symbols auffällt. Wird je Symbol geschrieben und entscheidet,
/// ob das Symbol in den Backtest darf.
/// </summary>
public sealed class DataQualityReport
{
    public string Symbol { get; set; } = string.Empty;

    public string Source { get; set; } = string.Empty;

    public Timeframe Timeframe { get; set; }

    public DateTime FromUtc { get; set; }

    public DateTime ToUtc { get; set; }

    public int BarCount { get; set; }

    public DateTime? FirstBarUtc { get; set; }

    public DateTime? LastBarUtc { get; set; }

    public int ExpectedBars { get; set; }

    public int MissingBars { get; set; }

    public decimal MissingPercent => ExpectedBars == 0 ? 0m : (decimal)MissingBars / ExpectedBars * 100m;

    /// <summary>Bars außerhalb jeder Session, z.B. vorbörslicher Handel. Kein Fehler, aber gut zu wissen.</summary>
    public int BarsOutsideSession { get; set; }

    public int IdenticalDuplicates { get; set; }

    public int ConflictingDuplicates { get; set; }

    public int OutOfOrderBars { get; set; }

    /// <summary>Bars mit auffällig großer Spanne gegenüber dem Median - Verdacht auf Bad Ticks.</summary>
    public int OutlierBars { get; set; }

    /// <summary>Bars ohne jede Bewegung. Bei liquiden Instrumenten ein Hinweis auf eingefrorene Kurse.</summary>
    public int FlatBars { get; set; }

    public int ZeroVolumeBars { get; set; }

    public int TradingDays { get; set; }

    public int HolidaysSkipped { get; set; }

    public List<DataGap> LargestGaps { get; } = new List<DataGap>();

    public List<string> Findings { get; } = new List<string>();

    public DataQualityStatus Status { get; set; } = DataQualityStatus.Good;

    public bool IsUsable => Status != DataQualityStatus.Rejected;

    public string ToMarkdown()
    {
        var text = new StringBuilder();
        text.AppendLine($"# Datenqualität: {Symbol}");
        text.AppendLine();
        text.AppendLine($"- **Status**: {Status}");
        text.AppendLine($"- Quelle: {Source}");
        text.AppendLine($"- Auflösung: {Timeframe}");
        text.AppendLine(Invariant($"- Zeitraum: {FromUtc:yyyy-MM-dd} bis {ToUtc:yyyy-MM-dd} ({TradingDays} Handelstage, {HolidaysSkipped} Feiertage übersprungen)"));
        text.AppendLine(Invariant($"- Bars: {BarCount} von {ExpectedBars} erwartet, {MissingBars} fehlen ({MissingPercent:0.00} %)"));

        if (FirstBarUtc.HasValue && LastBarUtc.HasValue)
        {
            text.AppendLine(Invariant($"- Erste Bar: {FirstBarUtc:yyyy-MM-dd HH:mm} UTC, letzte Bar: {LastBarUtc:yyyy-MM-dd HH:mm} UTC"));
        }

        text.AppendLine($"- Duplikate: {IdenticalDuplicates} identisch, {ConflictingDuplicates} widersprüchlich");
        text.AppendLine($"- Auffällige Bars: {OutlierBars} Ausreißer, {FlatBars} ohne Bewegung, {ZeroVolumeBars} ohne Volumen");
        text.AppendLine($"- Bars außerhalb der Session: {BarsOutsideSession}");

        if (LargestGaps.Count > 0)
        {
            text.AppendLine();
            text.AppendLine("## Größte Lücken");
            text.AppendLine();
            foreach (var gap in LargestGaps)
            {
                text.AppendLine($"- {gap}");
            }
        }

        if (Findings.Count > 0)
        {
            text.AppendLine();
            text.AppendLine("## Befunde");
            text.AppendLine();
            foreach (var finding in Findings)
            {
                text.AppendLine($"- {finding}");
            }
        }

        text.AppendLine();
        text.AppendLine("Lücken werden gemeldet und **nicht** interpoliert: erfundene Bars erzeugen Trades, die es nie gegeben hat.");
        return text.ToString();
    }

    private static string Invariant(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);

    public override string ToString() =>
        string.Format(
            CultureInfo.InvariantCulture,
            "{0}: {1}, {2} Bars, {3:0.00} % fehlen", Symbol, Status, BarCount, MissingPercent);
}

/// <summary>Ab wann Daten als brauchbar gelten. Bewusst konfigurierbar und im Report ausgewiesen.</summary>
public sealed class DataQualityThresholds
{
    /// <summary>Ab diesem Anteil fehlender Bars gibt es eine Warnung.</summary>
    public decimal WarnMissingPercent { get; set; } = 0.5m;

    /// <summary>Ab diesem Anteil fehlender Bars fliegt das Symbol aus dem Backtest.</summary>
    public decimal RejectMissingPercent { get; set; } = 5m;

    /// <summary>Spanne als Vielfaches der Median-Spanne, ab der eine Bar als Ausreißer gilt.</summary>
    public decimal OutlierRangeFactor { get; set; } = 20m;

    /// <summary>Ab diesem Anteil Ausreißer gibt es eine Warnung.</summary>
    public decimal WarnOutlierPercent { get; set; } = 0.5m;

    /// <summary>Mindestanzahl Bars, unter der eine Auswertung nicht belastbar ist.</summary>
    public int MinimumBars { get; set; } = 500;

    public static DataQualityThresholds Default => new DataQualityThresholds();
}
