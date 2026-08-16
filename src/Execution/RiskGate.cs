using System;
using System.Collections.Generic;
using System.Globalization;
using Daytrading.Strategies.Model;

namespace Daytrading.Execution;

/// <summary>
/// Entscheidet, ob aus einem Signal eine Order wird - und mit welcher Größe.
/// </summary>
/// <remarks>
/// Der Kern der Risikoschicht. Strategien können diese Prüfungen nicht umgehen, weil sie
/// weder Kontostand noch Positionsgröße kennen. Jede Ablehnung trägt einen typisierten Grund
/// und eine lesbare Begründung; stille Ablehnungen gibt es nicht.
///
/// Die zentrale Regel für parallele Positionen lautet:
/// <code>
/// Rückgang vom Tageshoch + Summe des offenen Risikos + Risiko des neuen Trades ≤ MaxOpenRiskPercent
/// </code>
/// Anders gesagt: Würden in diesem Moment alle Stops gleichzeitig auslösen, wäre die Tagesgrenze
/// immer noch eingehalten. Die Anzahl gleichzeitiger Positionen ergibt sich daraus von selbst.
///
/// Bezugsgröße aller Prozentwerte ist <see cref="AccountState.DayStartBalance"/>.
/// Die Größe wird aus <see cref="Signal.ReferencePrice"/> berechnet, also aus dem Close der
/// Signalbar. Da frühestens auf der Open der Folgebar ausgeführt wird, weicht der reale
/// Stopabstand leicht ab; der Host darf die Größe beim Fill mit demselben Risikobetrag neu
/// rechnen. Diese Differenz gehört zum Ausführungsproblem und wird dort gemessen.
/// </remarks>
public sealed class RiskGate
{
    private static readonly IReadOnlyList<OpenRiskItem> NoOpenRisk = new OpenRiskItem[0];

    private readonly ExecutionSymbol _symbol;
    private readonly RiskLimits _limits;

    public RiskGate(ExecutionSymbol symbol, RiskLimits limits)
    {
        _symbol = symbol ?? throw new ArgumentNullException(nameof(symbol));
        _limits = limits ?? throw new ArgumentNullException(nameof(limits));
        _limits.Validate();
    }

    /// <summary>
    /// Prüft ein Signal gegen alle Risikogrenzen.
    /// </summary>
    /// <param name="openRisk">
    /// Alle offenen Positionen des <b>Kontos</b> mit ihrem aktuellen Preis - nicht nur die
    /// dieses Symbols. Die Grenzen gelten je Konto.
    /// </param>
    /// <param name="riskMultiplier">
    /// Faktor der Bewertungsschicht auf das Risiko je Trade: 1.0 normal, 0.5 bei Degraded,
    /// 0 bei Suspended. Kürzt nur, erhöht nie.
    /// </param>
    public RiskDecision Evaluate(
        Signal? signal,
        MarketSnapshot snapshot,
        AccountState account,
        IReadOnlyList<OpenRiskItem>? openRisk = null,
        decimal riskMultiplier = 1m)
    {
        if (account == null)
        {
            throw new ArgumentNullException(nameof(account));
        }

        if (riskMultiplier < 0m || riskMultiplier > 1m)
        {
            throw new ArgumentOutOfRangeException(
                nameof(riskMultiplier), riskMultiplier, "Der Faktor der Bewertungsschicht darf nur kürzen, nie erhöhen.");
        }

        var positions = openRisk ?? NoOpenRisk;

        if (signal == null || signal.Kind != SignalKind.Entry || !signal.Direction.HasValue)
        {
            return RiskDecision.Reject(RiskRejectionReason.NotAnEntrySignal, "Kein Einstiegssignal.");
        }

        var direction = signal.Direction.Value;

        if (riskMultiplier == 0m)
        {
            return RiskDecision.Reject(
                RiskRejectionReason.StrategySuspended,
                "Die Bewertungsschicht hat diese Kombination abgeschaltet. Bestehende Positionen laufen zu Ende, " +
                "neue Trades gibt es erst nach bestandener Neuvalidierung.");
        }

        if (!signal.StopLoss.HasValue)
        {
            // Ein Signal ohne Stop wird abgelehnt und nicht mit einem Default-Stop ausgeführt:
            // die Positionsgröße käme sonst aus einer erfundenen Zahl.
            return RiskDecision.Reject(
                RiskRejectionReason.MissingStopLoss,
                "Einstiegssignal ohne Stop-Loss. Ein Default-Stop wird bewusst nicht ergänzt.");
        }

        var stopDistance = Math.Abs(signal.ReferencePrice - signal.StopLoss.Value);
        var stopIsOnCorrectSide = (signal.ReferencePrice - signal.StopLoss.Value) * direction.Sign() > 0m;
        if (!stopIsOnCorrectSide || stopDistance < _symbol.Info.TickSize)
        {
            return RiskDecision.Reject(
                RiskRejectionReason.InvalidStopLoss,
                Format(
                    "Stop {0} passt nicht zu einem {1}-Einstieg bei {2} oder liegt näher als ein Tick ({3}).",
                    signal.StopLoss.Value, direction, signal.ReferencePrice, _symbol.Info.TickSize));
        }

        if (!snapshot.IsInSession)
        {
            return RiskDecision.Reject(
                RiskRejectionReason.OutsideTradingWindow,
                Format("{0} UTC liegt außerhalb der Session {1}.", snapshot.BarCloseTimeUtc, snapshot.Session));
        }

        var minutesLeft = snapshot.TimeUntilSessionEnd.TotalMinutes;
        if (minutesLeft <= _limits.ForceFlatMinutesBeforeSessionEnd)
        {
            // Ein Einstieg, der binnen Minuten zwangsgeschlossen würde, ist kein Trade,
            // sondern nur Kosten.
            return RiskDecision.Reject(
                RiskRejectionReason.TooCloseToSessionEnd,
                Format(
                    "Nur noch {0:0} Minuten bis Sessionende, Zwangsschließung beginnt {1} Minuten vorher.",
                    minutesLeft, _limits.ForceFlatMinutesBeforeSessionEnd));
        }

        if (account.TotalDrawdownPercent >= _limits.MaxTotalDrawdownPercent)
        {
            return RiskDecision.Reject(
                RiskRejectionReason.TotalDrawdownLimit,
                Format(
                    "Gesamt-Drawdown {0:0.00}% erreicht die Grenze von {1}%. Die Strategie ist zu stoppen und zu prüfen.",
                    account.TotalDrawdownPercent, _limits.MaxTotalDrawdownPercent));
        }

        if (account.DailyLossPercent >= _limits.MaxDailyLossPercent)
        {
            return RiskDecision.Reject(
                RiskRejectionReason.DailyLossLimit,
                Format(
                    "Realisierter Tagesverlust {0:0.00}% erreicht die Grenze von {1}%. Heute kein neuer Trade.",
                    account.DailyLossPercent, _limits.MaxDailyLossPercent));
        }

        if (account.DailyDrawdownPercent >= _limits.MaxDailyDrawdownPercent)
        {
            return RiskDecision.Reject(
                RiskRejectionReason.DailyDrawdownLimit,
                Format(
                    "Tages-Drawdown {0:0.00}% erreicht die Grenze von {1}%. Heute kein neuer Trade.",
                    account.DailyDrawdownPercent, _limits.MaxDailyDrawdownPercent));
        }

        if (account.TradesToday >= _limits.MaxTradesPerDay)
        {
            return RiskDecision.Reject(
                RiskRejectionReason.MaxTradesPerDay,
                Format("Bereits {0} Trades heute, erlaubt sind {1}.", account.TradesToday, _limits.MaxTradesPerDay));
        }

        if (positions.Count >= _limits.MaxConcurrentPositions)
        {
            return RiskDecision.Reject(
                RiskRejectionReason.MaxConcurrentPositions,
                Format("Bereits {0} offene Positionen, erlaubt sind {1}.", positions.Count, _limits.MaxConcurrentPositions));
        }

        if (HasLosingPositionInSameDirection(positions, direction))
        {
            return RiskDecision.Reject(
                RiskRejectionReason.AveragingDownNotAllowed,
                Format(
                    "Es läuft bereits eine {0}-Position auf {1} im Verlust. Nachkaufen in Verlustpositionen ist gesperrt.",
                    direction, _symbol.Name));
        }

        var openRiskPercent = OpenRisk.TotalPercent(positions, account.DayStartBalance);
        var budgetFromOpenRisk = _limits.MaxOpenRiskPercent - account.DailyDrawdownPercent - openRiskPercent;
        var budgetFromDailyLoss = _limits.MaxDailyLossPercent - account.DailyLossPercent;
        var available = Math.Min(budgetFromOpenRisk, budgetFromDailyLoss);

        if (available <= 0m)
        {
            return RiskDecision.Reject(
                RiskRejectionReason.OpenRiskBudgetExhausted,
                Format(
                    "Kein Risikobudget mehr: Tages-Drawdown {0:0.00}% plus offenes Risiko {1:0.00}% schöpfen die Grenze " +
                    "von {2}% aus.",
                    account.DailyDrawdownPercent, openRiskPercent, _limits.MaxOpenRiskPercent));
        }

        var desired = Math.Min(signal.RiskPercent ?? _limits.RiskPerTradePercent, _limits.RiskPerTradePercent)
                      * riskMultiplier;
        var wasCapped = false;
        var riskPercent = desired;

        if (desired > available)
        {
            if (!_limits.CapRiskToRemainingDailyBudget)
            {
                return RiskDecision.Reject(
                    RiskRejectionReason.OpenRiskBudgetExhausted,
                    Format(
                        "Gewünschtes Risiko {0:0.00}% passt nicht in das verbleibende Budget von {1:0.00}%.",
                        desired, available));
            }

            riskPercent = available;
            wasCapped = true;
        }

        var riskAmount = riskPercent / 100m * account.DayStartBalance;
        var sizing = PositionSizer.Calculate(_symbol, riskAmount, stopDistance);

        if (!sizing.IsValid)
        {
            return RiskDecision.Reject(RiskRejectionReason.PositionTooSmallForRiskBudget, sizing.Message);
        }

        var effectivePercent = account.DayStartBalance <= 0m ? 0m : sizing.RiskAmount / account.DayStartBalance * 100m;

        return RiskDecision.Accept(
            sizing.Quantity,
            sizing.RiskAmount,
            effectivePercent,
            wasCapped,
            Format(
                "{0} {1} Einheiten {2}, Stopabstand {3}, Risiko {4} ({5:0.00}% von {6}){7}",
                direction, sizing.Quantity, _symbol.Name, stopDistance, sizing.RiskAmount,
                effectivePercent, account.DayStartBalance,
                wasCapped ? Format(", gekürzt von {0:0.00}% auf das Restbudget", desired) : string.Empty));
    }

    private bool HasLosingPositionInSameDirection(IReadOnlyList<OpenRiskItem> positions, TradeDirection direction)
    {
        for (var i = 0; i < positions.Count; i++)
        {
            var item = positions[i];
            var position = item.Position;
            if (position.Direction != direction || !string.Equals(position.Symbol, _symbol.Name, StringComparison.Ordinal))
            {
                continue;
            }

            var unrealized = (item.CurrentPrice - position.EntryPrice) * direction.Sign();
            if (unrealized < 0m)
            {
                return true;
            }
        }

        return false;
    }

    private static string Format(string template, params object[] args) =>
        string.Format(CultureInfo.InvariantCulture, template, args);
}
