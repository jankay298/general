using System;
using System.Collections.Generic;
using Daytrading.Strategies.Model;

namespace Daytrading.Execution.Sessions;

/// <summary>
/// Liefert die Handelssession eines Symbols für einen beliebigen Zeitpunkt.
/// </summary>
/// <remarks>
/// Die Session ist die Grundlage aller Tagesregeln: Eröffnungsrange, Zwangsschließung,
/// Tagesverlustgrenze und Trade-Zähler hängen daran.
///
/// Der Kalender liegt bewusst in der Ausführungsschicht und nicht in der Datenschicht: Backtester
/// <b>und</b> cBot müssen exakt dieselbe Sessiondefinition verwenden. Zwei Implementierungen
/// wären der sicherste Weg, im Livebetrieb zu einem anderen Zeitpunkt flat zu gehen als im Test.
/// </remarks>
public interface ISessionCalendar
{
    /// <summary>Ist an diesem Kalendertag (Ortszeit der Börse) Handel?</summary>
    bool IsTradingDay(DateTime localDate);

    /// <summary>Session dieses Handelstages, sofern gehandelt wird.</summary>
    bool TryGetSession(DateTime localDate, out TradingSession session);

    /// <summary>Session, die diesen UTC-Zeitpunkt enthält.</summary>
    bool TryGetSessionAt(DateTime utcInstant, out TradingSession session);

    /// <summary>Alle Sessions im Zeitraum, chronologisch.</summary>
    IEnumerable<TradingSession> Sessions(DateTime fromUtc, DateTime toUtc);
}

/// <summary>Sessionkalender aus Zeitzone, Handelszeiten, Handelstagen und Feiertagskalender.</summary>
public class TradingSessionCalendar : ISessionCalendar
{
    private readonly TimeZoneInfo _timeZone;
    private readonly TimeSpan _start;
    private readonly TimeSpan _end;
    private readonly HashSet<DayOfWeek> _tradingDays;
    private readonly IHolidayCalendar _holidays;

    public TradingSessionCalendar(
        TimeZoneInfo timeZone,
        TimeSpan sessionStart,
        TimeSpan sessionEnd,
        IEnumerable<DayOfWeek> tradingDays,
        IHolidayCalendar? holidays = null,
        string symbolName = "")
    {
        _timeZone = timeZone ?? throw new ArgumentNullException(nameof(timeZone));
        _start = sessionStart;
        _end = sessionEnd;
        _tradingDays = new HashSet<DayOfWeek>(tradingDays ?? throw new ArgumentNullException(nameof(tradingDays)));
        _holidays = holidays ?? NoHolidayCalendar.Instance;

        if (_tradingDays.Count == 0)
        {
            throw new ArgumentException($"{symbolName}: Ohne Handelstage gäbe es nie eine Session.", nameof(tradingDays));
        }

        if (_end <= _start)
        {
            // Sessions über Mitternacht hinaus wären mit den Daytrading-Regeln unvereinbar:
            // "flat vor Sessionende" und "kein Overnight" würden sich widersprechen.
            throw new ArgumentException(
                $"{symbolName}: Sessionende {sessionEnd} liegt nicht nach dem Beginn {sessionStart}. " +
                "Sessions über Mitternacht hinaus unterstützt das Framework bewusst nicht.",
                nameof(sessionEnd));
        }
    }

    public bool IsTradingDay(DateTime localDate)
    {
        var date = localDate.Date;
        return _tradingDays.Contains(date.DayOfWeek) && !_holidays.IsHoliday(date);
    }

    public bool TryGetSession(DateTime localDate, out TradingSession session)
    {
        var date = localDate.Date;
        if (!IsTradingDay(date))
        {
            session = default;
            return false;
        }

        var startUtc = ToUtc(date, _start);
        var endUtc = ToUtc(date, _end);
        session = new TradingSession(DateTime.SpecifyKind(date, DateTimeKind.Utc), startUtc, endUtc);
        return true;
    }

    public bool TryGetSessionAt(DateTime utcInstant, out TradingSession session)
    {
        if (utcInstant.Kind != DateTimeKind.Utc)
        {
            throw new ArgumentException("Zeitpunkt muss UTC sein.", nameof(utcInstant));
        }

        var local = TimeZoneInfo.ConvertTimeFromUtc(utcInstant, _timeZone).Date;

        // Ein Nachbartag kommt in Frage, wenn Ortszeit und UTC über die Datumsgrenze auseinanderfallen.
        for (var offset = -1; offset <= 1; offset++)
        {
            if (TryGetSession(local.AddDays(offset), out var candidate) && candidate.Contains(utcInstant))
            {
                session = candidate;
                return true;
            }
        }

        session = default;
        return false;
    }

    public IEnumerable<TradingSession> Sessions(DateTime fromUtc, DateTime toUtc)
    {
        if (fromUtc.Kind != DateTimeKind.Utc || toUtc.Kind != DateTimeKind.Utc)
        {
            throw new ArgumentException("Zeitraum muss UTC sein.", nameof(fromUtc));
        }

        var day = TimeZoneInfo.ConvertTimeFromUtc(fromUtc, _timeZone).Date.AddDays(-1);
        var last = TimeZoneInfo.ConvertTimeFromUtc(toUtc, _timeZone).Date.AddDays(1);

        while (day <= last)
        {
            if (TryGetSession(day, out var session) && session.EndUtc > fromUtc && session.StartUtc < toUtc)
            {
                yield return session;
            }

            day = day.AddDays(1);
        }
    }

    private DateTime ToUtc(DateTime localDate, TimeSpan timeOfDay)
    {
        // 24:00 meint Mitternacht des Folgetages; DateTime kann diese Uhrzeit nicht direkt tragen.
        var local = DateTime.SpecifyKind(localDate.Date, DateTimeKind.Unspecified) + timeOfDay;

        // Während der Zeitumstellung kann eine Ortszeit nicht existieren. ConvertTimeToUtc wirft
        // dann; wir weichen um eine Stunde aus, statt den Handelstag zu verlieren.
        if (_timeZone.IsInvalidTime(local))
        {
            local = local.AddHours(1);
        }

        return TimeZoneInfo.ConvertTimeToUtc(local, _timeZone);
    }
}
