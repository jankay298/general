using System;
using Daytrading.Execution.Sessions;

namespace Daytrading.Data.Config;

/// <summary>
/// Sessionkalender aus einer <see cref="SymbolConfig"/>.
/// </summary>
/// <remarks>
/// Nur die Übersetzung von Konfigurationstext in den Kalender der Ausführungsschicht. Die
/// Sessionlogik selbst liegt dort, damit Backtester und cBot dieselbe verwenden.
/// </remarks>
public sealed class SymbolSessionCalendar : TradingSessionCalendar
{
    public SymbolSessionCalendar(SymbolConfig config)
        : base(
            ConfigParser.ResolveTimeZone(Validated(config).TimeZone, config.Name),
            ConfigParser.ParseTimeOfDay(config.SessionStart, config.Name),
            ConfigParser.ParseTimeOfDay(config.SessionEnd, config.Name),
            ConfigParser.ParseTradingDays(config.TradingDays, config.AssetClass, config.Name),
            UsEquityHolidayCalendar.Resolve(config.HolidayCalendar),
            config.Name)
    {
    }

    private static SymbolConfig Validated(SymbolConfig config)
    {
        if (config == null)
        {
            throw new ArgumentNullException(nameof(config));
        }

        config.Validate();
        return config;
    }
}
