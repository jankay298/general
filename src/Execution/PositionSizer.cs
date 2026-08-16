using System;
using System.Globalization;

namespace Daytrading.Execution;

/// <summary>Ergebnis der Positionsgrößenrechnung.</summary>
public readonly struct SizingResult
{
    private SizingResult(bool isValid, decimal quantity, decimal riskAmount, string message)
    {
        IsValid = isValid;
        Quantity = quantity;
        RiskAmount = riskAmount;
        Message = message;
    }

    public bool IsValid { get; }

    public decimal Quantity { get; }

    /// <summary>Tatsächlicher Risikobetrag nach Rundung auf die Schrittweite. Nie größer als erlaubt.</summary>
    public decimal RiskAmount { get; }

    public string Message { get; }

    public static SizingResult Valid(decimal quantity, decimal riskAmount) =>
        new SizingResult(true, quantity, riskAmount, string.Empty);

    public static SizingResult Invalid(string message) =>
        new SizingResult(false, 0m, 0m, message);
}

/// <summary>
/// Rechnet aus erlaubtem Risikobetrag und Stopabstand die Positionsgröße.
/// </summary>
/// <remarks>
/// Die einzige Stelle im Framework, an der eine Positionsgröße entsteht. Feste Lotgrößen gibt
/// es nicht - die Größe ist immer das Ergebnis aus Risiko und Stopabstand:
///
/// <code>Größe = Risikobetrag / (Stopabstand × Punktwert je Einheit)</code>
///
/// Gerundet wird stets <b>nach unten</b> auf die Schrittweite des Symbols. Aufrunden würde das
/// erlaubte Risiko überschreiten - und zwar systematisch bei jedem einzelnen Trade.
/// </remarks>
public static class PositionSizer
{
    public static SizingResult Calculate(ExecutionSymbol symbol, decimal riskAmount, decimal stopDistance)
    {
        if (symbol == null)
        {
            throw new ArgumentNullException(nameof(symbol));
        }

        if (riskAmount <= 0m)
        {
            return SizingResult.Invalid(Format("Erlaubter Risikobetrag ist {0} und damit nicht handelbar.", riskAmount));
        }

        if (stopDistance <= 0m)
        {
            return SizingResult.Invalid(Format("Stopabstand ist {0}. Ohne Abstand ist keine Größe berechenbar.", stopDistance));
        }

        var moneyPerUnit = stopDistance * symbol.ValuePerPricePointPerUnit;
        var rawQuantity = riskAmount / moneyPerUnit;

        var steps = Math.Floor(rawQuantity / symbol.QuantityStep);
        var quantity = steps * symbol.QuantityStep;

        if (quantity > symbol.MaxQuantity)
        {
            quantity = Math.Floor(symbol.MaxQuantity / symbol.QuantityStep) * symbol.QuantityStep;
        }

        if (quantity < symbol.MinQuantity)
        {
            return SizingResult.Invalid(Format(
                "Bei einem Risiko von {0} und einem Stopabstand von {1} ergäben sich {2} Einheiten von {3}. " +
                "Die Mindestgröße ist {4} - dieser Trade würde das erlaubte Risiko überschreiten und wird abgelehnt.",
                riskAmount, stopDistance, rawQuantity, symbol.Name, symbol.MinQuantity));
        }

        return SizingResult.Valid(quantity, quantity * moneyPerUnit);
    }

    private static string Format(string template, params object[] args) =>
        string.Format(CultureInfo.InvariantCulture, template, args);
}
