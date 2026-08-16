using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Daytrading.Data.Config;
using Daytrading.Execution.Sessions;
using Daytrading.Data.Normalization;
using Daytrading.Strategies.Model;

namespace Daytrading.Data.Quality;

/// <summary>
/// Prüft eine Kursserie gegen den Sessionkalender des Symbols und schreibt den Befund.
/// </summary>
/// <remarks>
/// Der Maßstab sind die Sessions, nicht der Kalender: Ein Wochenende oder ein Feiertag ist
/// keine Lücke. Umgekehrt ist eine fehlende Stunde mitten in der Handelszeit eine, auch wenn
/// die Datei ansonsten vollständig aussieht.
/// </remarks>
public sealed class DataQualityAnalyzer
{
    private readonly DataQualityThresholds _thresholds;

    public DataQualityAnalyzer(DataQualityThresholds? thresholds = null)
    {
        _thresholds = thresholds ?? DataQualityThresholds.Default;
    }

    public DataQualityReport Analyze(
        string symbol,
        string source,
        Timeframe timeframe,
        IReadOnlyList<Candle> bars,
        ISessionCalendar calendar,
        DateTime fromUtc,
        DateTime toUtc,
        NormalizationReport? normalization = null)
    {
        if (bars == null)
        {
            throw new ArgumentNullException(nameof(bars));
        }

        if (calendar == null)
        {
            throw new ArgumentNullException(nameof(calendar));
        }

        var report = new DataQualityReport
        {
            Symbol = symbol,
            Source = source,
            Timeframe = timeframe,
            FromUtc = fromUtc,
            ToUtc = toUtc,
            BarCount = bars.Count,
            FirstBarUtc = bars.Count > 0 ? bars[0].OpenTimeUtc : (DateTime?)null,
            LastBarUtc = bars.Count > 0 ? bars[bars.Count - 1].OpenTimeUtc : (DateTime?)null,
        };

        if (normalization != null)
        {
            report.IdenticalDuplicates = normalization.IdenticalDuplicates;
            report.ConflictingDuplicates = normalization.ConflictingDuplicates;
            report.OutOfOrderBars = normalization.OutOfOrderBars;

            foreach (var conflict in normalization.Conflicts)
            {
                report.Findings.Add($"Widersprüchliches Duplikat: {conflict}");
            }
        }

        var actual = new HashSet<DateTime>();
        foreach (var bar in bars)
        {
            actual.Add(bar.OpenTimeUtc);
        }

        var expected = ExpectedTimestamps(calendar, timeframe, fromUtc, toUtc, report);
        var missing = new List<DateTime>();
        foreach (var timestamp in expected)
        {
            if (!actual.Contains(timestamp))
            {
                missing.Add(timestamp);
            }
        }

        report.ExpectedBars = expected.Count;
        report.MissingBars = missing.Count;
        report.BarsOutsideSession = actual.Count(timestamp => !expected.Contains(timestamp));

        CollectGaps(missing, timeframe, report);
        InspectBars(bars, report);
        Judge(report);

        return report;
    }

    private HashSet<DateTime> ExpectedTimestamps(
        ISessionCalendar calendar,
        Timeframe timeframe,
        DateTime fromUtc,
        DateTime toUtc,
        DataQualityReport report)
    {
        var expected = new HashSet<DateTime>();
        var tradingDays = 0;

        foreach (var session in calendar.Sessions(fromUtc, toUtc))
        {
            tradingDays++;
            for (var time = session.StartUtc; time + timeframe.Duration <= session.EndUtc; time += timeframe.Duration)
            {
                if (time >= fromUtc && time < toUtc)
                {
                    expected.Add(time);
                }
            }
        }

        report.TradingDays = tradingDays;
        report.HolidaysSkipped = CountSkippedWeekdays(calendar, fromUtc, toUtc);
        return expected;
    }

    private static int CountSkippedWeekdays(ISessionCalendar calendar, DateTime fromUtc, DateTime toUtc)
    {
        // Werktage ohne Session sind in aller Regel Feiertage. Reine Wochenendtage zählen nicht mit.
        var skipped = 0;
        for (var day = fromUtc.Date; day < toUtc.Date; day = day.AddDays(1))
        {
            if (day.DayOfWeek == DayOfWeek.Saturday || day.DayOfWeek == DayOfWeek.Sunday)
            {
                continue;
            }

            if (!calendar.IsTradingDay(day))
            {
                skipped++;
            }
        }

        return skipped;
    }

    private static void CollectGaps(List<DateTime> missing, Timeframe timeframe, DataQualityReport report)
    {
        if (missing.Count == 0)
        {
            return;
        }

        missing.Sort();
        var gaps = new List<DataGap>();
        var start = missing[0];
        var previous = missing[0];
        var count = 1;

        for (var i = 1; i < missing.Count; i++)
        {
            if (missing[i] - previous == timeframe.Duration)
            {
                previous = missing[i];
                count++;
                continue;
            }

            gaps.Add(new DataGap(start, previous + timeframe.Duration, count));
            start = missing[i];
            previous = missing[i];
            count = 1;
        }

        gaps.Add(new DataGap(start, previous + timeframe.Duration, count));

        report.LargestGaps.AddRange(gaps.OrderByDescending(gap => gap.MissingBars).ThenBy(gap => gap.FromUtc).Take(10));
    }

    private void InspectBars(IReadOnlyList<Candle> bars, DataQualityReport report)
    {
        if (bars.Count == 0)
        {
            return;
        }

        var ranges = new List<decimal>(bars.Count);
        foreach (var bar in bars)
        {
            ranges.Add(bar.Range);

            if (bar.Range == 0m)
            {
                report.FlatBars++;
            }

            if (bar.Volume == 0m)
            {
                report.ZeroVolumeBars++;
            }
        }

        ranges.Sort();
        var median = ranges[ranges.Count / 2];
        if (median <= 0m)
        {
            return;
        }

        var limit = median * _thresholds.OutlierRangeFactor;
        var worst = default(Candle);
        var worstRange = 0m;

        foreach (var bar in bars)
        {
            if (bar.Range <= limit)
            {
                continue;
            }

            report.OutlierBars++;
            if (bar.Range > worstRange)
            {
                worstRange = bar.Range;
                worst = bar;
            }
        }

        if (report.OutlierBars > 0)
        {
            report.Findings.Add(string.Format(
                CultureInfo.InvariantCulture,
                "{0} Bars mit einer Spanne über dem {1}-fachen des Medians ({2}). Größte: {3}.",
                report.OutlierBars, _thresholds.OutlierRangeFactor, median, worst));
        }
    }

    private void Judge(DataQualityReport report)
    {
        if (report.BarCount == 0)
        {
            report.Status = DataQualityStatus.Rejected;
            report.Findings.Add("Keine einzige Bar im angefragten Zeitraum.");
            return;
        }

        if (report.ConflictingDuplicates > 0)
        {
            report.Status = DataQualityStatus.Rejected;
            report.Findings.Add(
                $"{report.ConflictingDuplicates} widersprüchliche Duplikate. Das deutet auf vermischte Quellen hin - " +
                "solche Daten werden nicht stillschweigend geglättet.");
            return;
        }

        if (report.BarCount < _thresholds.MinimumBars)
        {
            report.Status = DataQualityStatus.Rejected;
            report.Findings.Add(
                $"Nur {report.BarCount} Bars, mindestens {_thresholds.MinimumBars} sind nötig, damit eine Auswertung trägt.");
            return;
        }

        if (report.MissingPercent > _thresholds.RejectMissingPercent)
        {
            report.Status = DataQualityStatus.Rejected;
            report.Findings.Add(string.Format(
                CultureInfo.InvariantCulture,
                "{0:0.00} % der erwarteten Bars fehlen, erlaubt sind {1} %.",
                report.MissingPercent, _thresholds.RejectMissingPercent));
            return;
        }

        var warned = false;

        if (report.MissingPercent > _thresholds.WarnMissingPercent)
        {
            warned = true;
            report.Findings.Add(string.Format(
                CultureInfo.InvariantCulture,
                "{0:0.00} % der erwarteten Bars fehlen. Nutzbar, aber im Ergebnis mit auszuweisen.",
                report.MissingPercent));
        }

        var outlierPercent = report.BarCount == 0 ? 0m : (decimal)report.OutlierBars / report.BarCount * 100m;
        if (outlierPercent > _thresholds.WarnOutlierPercent)
        {
            warned = true;
        }

        report.Status = warned ? DataQualityStatus.Warning : DataQualityStatus.Good;
    }
}
