using System;
using System.Collections.Generic;
using Daytrading.Strategies.Model;

namespace Daytrading.Data.Config;

/// <summary>
/// Liefert die Handelssession eines Symbols für einen beliebigen Zeitpunkt.
/// </summary>
/// <remarks>
/// Die Session ist die Grundlage aller Tagesregeln: Eröffnungsrange, Zwangsschließung,
/// Tagesverlustgrenze und Trade-Zähler hängen daran. Sie kommt aus der Symbolkonfiguration
/// und wird vom Host berechnet - Strategien bekommen nur das fertige Ergebnis.
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

/// <summary>Sessionkalender aus einer <see cref="SymbolConfig"/>.</summary>
public sealed class SymbolSessionCalendar : ISessionCalendar
{
    private readonly TimeZoneInfo _timeZone;
    private readonly TimeSpan _start;
    private readonly TimeSpan _end;
    private readonly HashSet<DayOfWeek> _tradingDays;
    private readonly IHolidayCalendar _holidays;

    public SymbolSessionCalendar(SymbolConfig config)
    {
        if (config == null)
        {
            throw new ArgumentNullException(nameof(config));
        }

        config.Validate();

        _timeZone = ConfigParser.ResolveTimeZone(config.TimeZone, config.Name);
        _start = ConfigParser.ParseTimeOfDay(config.SessionStart, config.Name);
        _end = ConfigParser.ParseTimeOfDay(config.SessionEnd, config.Name);
        _tradingDays = new HashSet<DayOfWeek>(ConfigParser.ParseTradingDays(config.TradingDays, config.AssetClass, config.Name));
        _holidays = UsEquityHolidayCalendar.Resolve(config.HolidayCalendar);

        if (_end <= _start)
        {
            // Sessions über Mitternacht hinaus wären mit den Daytrading-Regeln unvereinbar:
            // "flat vor Sessionende" und "kein Overnight" würden sich widersprechen.
            throw new MarketDataException(
                $"{config.Name}: Sessionende {config.SessionEnd} liegt nicht nach dem Beginn {config.SessionStart}. " +
                "Sessions über Mitternacht hinaus unterstützt das Framework bewusst nicht.");
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

        // Während der Zeitumstellung kann eine Ortszeit doppelt oder gar nicht existieren.
        // ConvertTimeToUtc wirft dann; wir weichen um eine Stunde aus, statt den Tag zu verlieren.
        if (_timeZone.IsInvalidTime(local))
        {
            local = local.AddHours(1);
        }

        return TimeZoneInfo.ConvertTimeToUtc(local, _timeZone);
    }
}
