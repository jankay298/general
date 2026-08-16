using System;

namespace Daytrading.Strategies;

public enum StrategyLogLevel
{
    Debug = 0,
    Info = 1,
    Warning = 2,
    Error = 3,
}

/// <summary>
/// Protokollsenke für Strategien. Abstrakt, damit dieselbe Strategie im Backtester in
/// eine Datei und im cBot in das cTrader-Log schreibt.
/// </summary>
/// <remarks>
/// Stille Fehler sind im Backtest besonders teuer, weil sie als plausibles Ergebnis
/// erscheinen. Alles, was eine Strategie zum Verwerfen eines Signals bewegt, gehört hier hin.
/// </remarks>
public interface IStrategyLog
{
    void Write(StrategyLogLevel level, string message);
}

/// <summary>Verwirft alle Meldungen. Default, wenn kein Log gesetzt ist.</summary>
public sealed class NullStrategyLog : IStrategyLog
{
    public static readonly NullStrategyLog Instance = new NullStrategyLog();

    private NullStrategyLog()
    {
    }

    public void Write(StrategyLogLevel level, string message)
    {
    }
}

/// <summary>Sammelt Meldungen im Speicher - für Tests und für Diagnose einzelner Backtest-Läufe.</summary>
public sealed class InMemoryStrategyLog : IStrategyLog
{
    private readonly System.Collections.Generic.List<string> _entries = new System.Collections.Generic.List<string>();

    public System.Collections.Generic.IReadOnlyList<string> Entries => _entries;

    public void Write(StrategyLogLevel level, string message) => _entries.Add($"[{level}] {message}");
}

public static class StrategyLogExtensions
{
    public static void Debug(this IStrategyLog log, string message) => Write(log, StrategyLogLevel.Debug, message);

    public static void Info(this IStrategyLog log, string message) => Write(log, StrategyLogLevel.Info, message);

    public static void Warning(this IStrategyLog log, string message) => Write(log, StrategyLogLevel.Warning, message);

    public static void Error(this IStrategyLog log, string message) => Write(log, StrategyLogLevel.Error, message);

    private static void Write(IStrategyLog log, StrategyLogLevel level, string message)
    {
        if (log == null)
        {
            throw new ArgumentNullException(nameof(log));
        }

        log.Write(level, message);
    }
}
