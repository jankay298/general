using System.Globalization;

namespace Daytrading.Execution;

/// <summary>Warum ein Signal nicht zu einer Order wurde. Jede Ablehnung wird protokolliert.</summary>
public enum RiskRejectionReason
{
    None = 0,
    NotAnEntrySignal,
    MissingStopLoss,
    InvalidStopLoss,
    OutsideTradingWindow,
    TooCloseToSessionEnd,
    TotalDrawdownLimit,
    DailyLossLimit,
    DailyDrawdownLimit,
    MaxTradesPerDay,
    MaxConcurrentPositions,
    AveragingDownNotAllowed,
    OpenRiskBudgetExhausted,
    PositionTooSmallForRiskBudget,

    /// <summary>Spread und Kommission fressen einen zu großen Teil des geplanten Risikos.</summary>
    CostTooHighForStopDistance,

    /// <summary>Die Bewertungsschicht hat die Kombination abgeschaltet - kein neuer Trade.</summary>
    StrategySuspended,
}

/// <summary>Ergebnis der Risikoprüfung eines Einstiegssignals.</summary>
public sealed class RiskDecision
{
    private RiskDecision(
        bool accepted,
        decimal quantity,
        decimal riskAmount,
        decimal riskPercent,
        RiskRejectionReason reason,
        string message)
    {
        Accepted = accepted;
        Quantity = quantity;
        RiskAmount = riskAmount;
        RiskPercent = riskPercent;
        Reason = reason;
        Message = message;
    }

    public bool Accepted { get; }

    public decimal Quantity { get; }

    /// <summary>Risikobetrag in Kontowährung nach Rundung auf die Schrittweite.</summary>
    public decimal RiskAmount { get; }

    /// <summary>Tatsächlich eingesetztes Risiko in Prozent des Kontostands bei Tagesbeginn.</summary>
    public decimal RiskPercent { get; }

    public RiskRejectionReason Reason { get; }

    public string Message { get; }

    /// <summary>True, wenn das Risiko gegenüber dem Wunsch der Strategie gekürzt wurde.</summary>
    public bool WasCapped { get; private set; }

    public static RiskDecision Accept(decimal quantity, decimal riskAmount, decimal riskPercent, bool wasCapped, string message) =>
        new RiskDecision(true, quantity, riskAmount, riskPercent, RiskRejectionReason.None, message) { WasCapped = wasCapped };

    public static RiskDecision Reject(RiskRejectionReason reason, string message) =>
        new RiskDecision(false, 0m, 0m, 0m, reason, message);

    public override string ToString() =>
        Accepted
            ? string.Format(
                CultureInfo.InvariantCulture,
                "angenommen: {0} Einheiten, Risiko {1} ({2:0.00}%){3}",
                Quantity, RiskAmount, RiskPercent, WasCapped ? " [gekürzt]" : string.Empty)
            : $"abgelehnt ({Reason}): {Message}";
}
