using System;
using System.Globalization;
using Daytrading.Strategies.Model;

namespace Daytrading.Execution;

public enum ExecutionInstructionKind
{
    OpenPosition = 0,
    ClosePosition = 1,
}

/// <summary>Grund für das Ende einer Position. Landet unverändert im Trade-Log.</summary>
public enum ExitReason
{
    /// <summary>Stop-Loss ausgelöst. Wird von der Ausführungssimulation bzw. vom Broker gemeldet.</summary>
    StopLoss = 0,

    /// <summary>Kursziel erreicht.</summary>
    TakeProfit = 1,

    /// <summary>Die Strategie hat den Ausstieg verlangt.</summary>
    StrategyExit = 2,

    /// <summary>Zwangsschließung vor Sessionende.</summary>
    SessionEnd = 3,

    /// <summary>Maximale Haltedauer erreicht.</summary>
    MaxHoldingTime = 4,

    /// <summary>Position lief entgegen der Regel über einen Handelstag hinaus und wurde geschlossen.</summary>
    OvernightGuard = 5,

    /// <summary>Eine Risikogrenze hat die Schließung erzwungen.</summary>
    RiskLimit = 6,

    /// <summary>Ende der Daten im Backtest. Für die Auswertung als Sonderfall zu kennzeichnen.</summary>
    EndOfData = 7,
}

/// <summary>
/// Eine Anweisung der Ausführungsschicht an den Host.
/// </summary>
/// <remarks>
/// Die Ausführungsschicht entscheidet, führt aber nicht selbst aus. Sie kennt weder Broker-API
/// noch Fill-Preise; sie liefert Anweisungen, die der Backtester gegen sein Kostenmodell und der
/// cBot gegen cTrader ausführt. Dadurch treffen Backtest und Livebetrieb garantiert dieselben
/// Entscheidungen - und sie lassen sich ohne Broker testen.
/// </remarks>
public sealed class ExecutionInstruction
{
    private ExecutionInstruction(
        ExecutionInstructionKind kind,
        DateTime decisionTimeUtc,
        string symbol,
        TradeDirection? direction,
        decimal quantity,
        decimal referencePrice,
        decimal? stopLoss,
        decimal? takeProfit,
        decimal riskAmount,
        decimal riskPercent,
        string? positionId,
        ExitReason? exitReason,
        string reason)
    {
        Kind = kind;
        DecisionTimeUtc = decisionTimeUtc;
        Symbol = symbol;
        Direction = direction;
        Quantity = quantity;
        ReferencePrice = referencePrice;
        StopLoss = stopLoss;
        TakeProfit = takeProfit;
        RiskAmount = riskAmount;
        RiskPercent = riskPercent;
        PositionId = positionId;
        ExitReason = exitReason;
        Reason = reason;
    }

    public ExecutionInstructionKind Kind { get; }

    /// <summary>Barschluss, zu dem entschieden wurde. Ausgeführt wird frühestens auf der Open der Folgebar.</summary>
    public DateTime DecisionTimeUtc { get; }

    public string Symbol { get; }

    public TradeDirection? Direction { get; }

    public decimal Quantity { get; }

    public decimal ReferencePrice { get; }

    public decimal? StopLoss { get; }

    public decimal? TakeProfit { get; }

    /// <summary>Geplanter Risikobetrag in Kontowährung.</summary>
    public decimal RiskAmount { get; }

    /// <summary>Geplantes Risiko in Prozent des Kontostands bei Tagesbeginn.</summary>
    public decimal RiskPercent { get; }

    public string? PositionId { get; }

    public ExitReason? ExitReason { get; }

    /// <summary>Lesbare Begründung für das Log.</summary>
    public string Reason { get; }

    public static ExecutionInstruction Open(
        DateTime decisionTimeUtc,
        string symbol,
        TradeDirection direction,
        decimal quantity,
        decimal referencePrice,
        decimal stopLoss,
        decimal? takeProfit,
        decimal riskAmount,
        decimal riskPercent,
        string reason)
    {
        if (quantity <= 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(quantity), quantity, "Positionsgröße muss positiv sein.");
        }

        return new ExecutionInstruction(
            ExecutionInstructionKind.OpenPosition, decisionTimeUtc, symbol, direction, quantity, referencePrice,
            stopLoss, takeProfit, riskAmount, riskPercent, null, null, reason);
    }

    public static ExecutionInstruction Close(
        DateTime decisionTimeUtc,
        string symbol,
        string positionId,
        decimal referencePrice,
        ExitReason exitReason,
        string reason)
    {
        if (string.IsNullOrWhiteSpace(positionId))
        {
            throw new ArgumentException("Positions-Id darf nicht leer sein.", nameof(positionId));
        }

        return new ExecutionInstruction(
            ExecutionInstructionKind.ClosePosition, decisionTimeUtc, symbol, null, 0m, referencePrice,
            null, null, 0m, 0m, positionId, exitReason, reason);
    }

    public override string ToString() =>
        Kind == ExecutionInstructionKind.OpenPosition
            ? string.Format(
                CultureInfo.InvariantCulture,
                "OPEN {0} {1} x{2} @{3} SL={4} TP={5} ({6})",
                Symbol, Direction, Quantity, ReferencePrice, StopLoss,
                TakeProfit.HasValue ? TakeProfit.Value.ToString(CultureInfo.InvariantCulture) : "-", Reason)
            : string.Format(
                CultureInfo.InvariantCulture,
                "CLOSE {0} #{1} @{2} [{3}] ({4})",
                Symbol, PositionId, ReferencePrice, ExitReason, Reason);
}
