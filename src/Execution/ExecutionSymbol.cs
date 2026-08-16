using System;
using Daytrading.Strategies.Model;

namespace Daytrading.Execution;

/// <summary>
/// Handelbare Eigenschaften eines Symbols: alles, was zur Ausführung gebraucht wird und was
/// eine Strategie bewusst nicht sehen darf.
/// </summary>
/// <remarks>
/// Kommt aus der Symbolkonfiguration, nicht aus dem Code. Spread und Kommission werden hier
/// mitgeführt, aber nicht von der Positionsgrößenrechnung verwendet - sie gehören zum
/// Kostenmodell der Ausführungssimulation und stehen deshalb an derselben Stelle wie die
/// übrigen Symboleigenschaften.
/// </remarks>
public sealed class ExecutionSymbol
{
    public ExecutionSymbol(
        SymbolInfo info,
        decimal minQuantity,
        decimal quantityStep,
        decimal maxQuantity,
        decimal valuePerPricePointPerUnit = 1m,
        decimal typicalSpread = 0m,
        decimal commissionPerUnitPerSide = 0m)
    {
        Info = info ?? throw new ArgumentNullException(nameof(info));

        if (minQuantity <= 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(minQuantity), minQuantity, "Mindestgröße muss positiv sein.");
        }

        if (quantityStep <= 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(quantityStep), quantityStep, "Schrittweite muss positiv sein.");
        }

        if (maxQuantity < minQuantity)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxQuantity), maxQuantity, $"Maximalgröße muss mindestens der Mindestgröße {minQuantity} entsprechen.");
        }

        if (valuePerPricePointPerUnit <= 0m)
        {
            throw new ArgumentOutOfRangeException(
                nameof(valuePerPricePointPerUnit), valuePerPricePointPerUnit, "Punktwert muss positiv sein.");
        }

        if (typicalSpread < 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(typicalSpread), typicalSpread, "Spread darf nicht negativ sein.");
        }

        if (commissionPerUnitPerSide < 0m)
        {
            throw new ArgumentOutOfRangeException(
                nameof(commissionPerUnitPerSide), commissionPerUnitPerSide, "Kommission darf nicht negativ sein.");
        }

        MinQuantity = minQuantity;
        QuantityStep = quantityStep;
        MaxQuantity = maxQuantity;
        ValuePerPricePointPerUnit = valuePerPricePointPerUnit;
        TypicalSpread = typicalSpread;
        CommissionPerUnitPerSide = commissionPerUnitPerSide;
    }

    public SymbolInfo Info { get; }

    public string Name => Info.Name;

    public decimal MinQuantity { get; }

    /// <summary>Erlaubte Schrittweite der Positionsgröße. Gerundet wird immer nach unten.</summary>
    public decimal QuantityStep { get; }

    public decimal MaxQuantity { get; }

    /// <summary>
    /// Wert einer Preisbewegung von 1.0 je gehandelter Einheit, in Kontowährung.
    /// Deckt Kontraktgröße und Währungsumrechnung in einer Zahl ab. Für eine in Kontowährung
    /// notierte Aktie ist der Wert 1.
    /// </summary>
    public decimal ValuePerPricePointPerUnit { get; }

    /// <summary>Typischer Spread in Preiseinheiten. Für das Kostenmodell, nicht für die Größenrechnung.</summary>
    public decimal TypicalSpread { get; }

    /// <summary>Kommission je Einheit und Handelsrichtung, in Kontowährung.</summary>
    public decimal CommissionPerUnitPerSide { get; }

    /// <summary>Verlust in Kontowährung, wenn sich der Preis um <paramref name="priceDistance"/> gegen die Position bewegt.</summary>
    public decimal MoneyPerPriceDistance(decimal priceDistance, decimal quantity) =>
        Math.Abs(priceDistance) * quantity * ValuePerPricePointPerUnit;

    public override string ToString() => $"{Name} (min {MinQuantity}, Schritt {QuantityStep}, Punktwert {ValuePerPricePointPerUnit})";
}
