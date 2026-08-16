using System;
using System.Collections.Generic;
using System.Globalization;
using Daytrading.Strategies.Model;

namespace Daytrading.Execution;

/// <summary>
/// Erzwingt die harten Daytrading-Regeln: flat vor Sessionende, keine Position über Nacht,
/// optionale maximale Haltedauer.
/// </summary>
/// <remarks>
/// Diese Regeln liegen hier und nicht in den Strategien, weil eine einzelne Strategie sie sonst
/// versehentlich umgehen könnte. Der Guard entscheidet ausschließlich anhand der Zeit; ob eine
/// Position im Gewinn oder Verlust steht, spielt keine Rolle.
/// </remarks>
public sealed class SessionGuard
{
    private readonly RiskLimits _limits;

    public SessionGuard(RiskLimits limits)
    {
        _limits = limits ?? throw new ArgumentNullException(nameof(limits));
    }

    /// <summary>
    /// Ermittelt, welche der offenen Positionen zum aktuellen Barschluss zu schließen sind.
    /// </summary>
    public void AppendExits(
        MarketSnapshot snapshot,
        IReadOnlyList<Position> openPositions,
        List<ExecutionInstruction> target)
    {
        if (target == null)
        {
            throw new ArgumentNullException(nameof(target));
        }

        if (openPositions == null || openPositions.Count == 0)
        {
            return;
        }

        var now = snapshot.BarCloseTimeUtc;
        var price = snapshot.Current.Close;
        var minutesLeft = snapshot.TimeUntilSessionEnd.TotalMinutes;
        var forceFlat = !snapshot.IsInSession || minutesLeft <= _limits.ForceFlatMinutesBeforeSessionEnd;

        for (var i = 0; i < openPositions.Count; i++)
        {
            var position = openPositions[i];

            if (forceFlat)
            {
                target.Add(ExecutionInstruction.Close(
                    now,
                    position.Symbol,
                    position.Id,
                    price,
                    ExitReason.SessionEnd,
                    Format(
                        "Zwangsschließung: noch {0:0} Minuten bis Sessionende (Grenze {1} Minuten).",
                        Math.Max(minutesLeft, 0), _limits.ForceFlatMinutesBeforeSessionEnd)));
                continue;
            }

            if (position.EntryTimeUtc < snapshot.Session.StartUtc)
            {
                // Sollte nach der Zwangsschließung nicht mehr vorkommen. Bleibt als Netz, damit
                // eine übersehene Position nicht still über Nacht weiterläuft.
                target.Add(ExecutionInstruction.Close(
                    now,
                    position.Symbol,
                    position.Id,
                    price,
                    ExitReason.OvernightGuard,
                    Format(
                        "Position stammt vom {0:yyyy-MM-dd} und läuft in den Handelstag {1:yyyy-MM-dd} hinein.",
                        position.EntryTimeUtc, snapshot.Session.TradingDay)));
                continue;
            }

            if (_limits.MaxHoldingMinutes.HasValue)
            {
                var holding = position.HoldingTime(now);
                if (holding.TotalMinutes >= _limits.MaxHoldingMinutes.Value)
                {
                    target.Add(ExecutionInstruction.Close(
                        now,
                        position.Symbol,
                        position.Id,
                        price,
                        ExitReason.MaxHoldingTime,
                        Format(
                            "Maximale Haltedauer erreicht: {0:0} von {1} Minuten.",
                            holding.TotalMinutes, _limits.MaxHoldingMinutes.Value)));
                }
            }
        }
    }

    private static string Format(string template, params object[] args) =>
        string.Format(CultureInfo.InvariantCulture, template, args);
}
