using System;
using System.Collections.Generic;

namespace Daytrading.Execution.Sessions;

/// <summary>
/// Börsenfeiertage. Ohne diese Unterscheidung würde jeder Feiertag als Datenlücke gemeldet -
/// und ein sauberes Symbol sähe aus wie ein kaputtes.
/// </summary>
public interface IHolidayCalendar
{
    string Name { get; }

    bool IsHoliday(DateTime localDate);
}

/// <summary>Für Märkte ohne Feiertage, insbesondere Krypto.</summary>
public sealed class NoHolidayCalendar : IHolidayCalendar
{
    public static readonly NoHolidayCalendar Instance = new NoHolidayCalendar();

    private NoHolidayCalendar()
    {
    }

    public string Name => "none";

    public bool IsHoliday(DateTime localDate) => false;
}

/// <summary>
/// Feiertage der US-Aktienbörsen, aus Regeln berechnet statt aus einer gepflegten Liste.
/// </summary>
/// <remarks>
/// Enthält die zehn regulären Feiertage von NYSE und Nasdaq. Bewusst <b>nicht</b> enthalten
/// sind verkürzte Handelstage (z.B. der Tag nach Thanksgiving) und außerordentliche
/// Schließungen. Verkürzte Tage tauchen im Qualitätsreport als fehlende Bars auf; das ist
/// gewollt, weil sie für eine Eröffnungsrange-Strategie tatsächlich andere Bedingungen bedeuten.
/// </remarks>
public sealed class UsEquityHolidayCalendar : IHolidayCalendar
{
    private readonly Dictionary<int, HashSet<DateTime>> _cache = new Dictionary<int, HashSet<DateTime>>();

    public string Name => "us-equity";

    public bool IsHoliday(DateTime localDate)
    {
        var date = localDate.Date;
        if (!_cache.TryGetValue(date.Year, out var holidays))
        {
            holidays = Build(date.Year);
            _cache[date.Year] = holidays;
        }

        return holidays.Contains(date);
    }

    private static HashSet<DateTime> Build(int year)
    {
        var days = new HashSet<DateTime>
        {
            Observed(new DateTime(year, 1, 1)),                  // Neujahr
            NthWeekday(year, 1, DayOfWeek.Monday, 3),            // Martin Luther King Jr. Day
            NthWeekday(year, 2, DayOfWeek.Monday, 3),            // Washington's Birthday
            GoodFriday(year),
            LastWeekday(year, 5, DayOfWeek.Monday),              // Memorial Day
            Observed(new DateTime(year, 7, 4)),                  // Independence Day
            NthWeekday(year, 9, DayOfWeek.Monday, 1),            // Labor Day
            NthWeekday(year, 11, DayOfWeek.Thursday, 4),         // Thanksgiving
            Observed(new DateTime(year, 12, 25)),                // Weihnachten
        };

        if (year >= 2022)
        {
            days.Add(Observed(new DateTime(year, 6, 19)));       // Juneteenth, erst ab 2022 Börsenfeiertag
        }

        return days;
    }

    /// <summary>Fällt ein Feiertag auf ein Wochenende, ruht der Handel am angrenzenden Werktag.</summary>
    private static DateTime Observed(DateTime date) => date.DayOfWeek switch
    {
        DayOfWeek.Saturday => date.AddDays(-1),
        DayOfWeek.Sunday => date.AddDays(1),
        _ => date,
    };

    private static DateTime NthWeekday(int year, int month, DayOfWeek weekday, int count)
    {
        var date = new DateTime(year, month, 1);
        var offset = ((int)weekday - (int)date.DayOfWeek + 7) % 7;
        return date.AddDays(offset + 7 * (count - 1));
    }

    private static DateTime LastWeekday(int year, int month, DayOfWeek weekday)
    {
        var date = new DateTime(year, month, DateTime.DaysInMonth(year, month));
        var offset = ((int)date.DayOfWeek - (int)weekday + 7) % 7;
        return date.AddDays(-offset);
    }

    /// <summary>Karfreitag, zwei Tage vor Ostersonntag nach dem gregorianischen Osteralgorithmus.</summary>
    private static DateTime GoodFriday(int year)
    {
        var a = year % 19;
        var b = year / 100;
        var c = year % 100;
        var d = b / 4;
        var e = b % 4;
        var f = (b + 8) / 25;
        var g = (b - f + 1) / 3;
        var h = (19 * a + b - d - g + 15) % 30;
        var i = c / 4;
        var k = c % 4;
        var l = (32 + 2 * e + 2 * i - h - k) % 7;
        var m = (a + 11 * h + 22 * l) / 451;
        var month = (h + l - 7 * m + 114) / 31;
        var day = (h + l - 7 * m + 114) % 31 + 1;
        return new DateTime(year, month, day).AddDays(-2);
    }

    public static IHolidayCalendar Resolve(string name) => name?.ToLowerInvariant() switch
    {
        null => NoHolidayCalendar.Instance,
        "" => NoHolidayCalendar.Instance,
        "none" => NoHolidayCalendar.Instance,
        "us-equity" => new UsEquityHolidayCalendar(),
        _ => throw new ArgumentException(
            $"Unbekannter Feiertagskalender '{name}'. Bekannt sind 'none' und 'us-equity'.", nameof(name)),
    };
}
