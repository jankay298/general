using System;
using System.Collections.Generic;
using Daytrading.Strategies.Model;

namespace Daytrading.Execution;

/// <summary>
/// Eine offene Position mit ihrem aktuellen Preis - die Eingabe für die Berechnung des
/// gleichzeitig offenen Risikos.
/// </summary>
/// <remarks>
/// Das Risiko wird bewusst vom <b>aktuellen</b> Preis bis zum Stop gemessen, nicht vom
/// Einstieg. Der Teil zwischen Einstieg und aktuellem Preis ist bereits in der Equity und
/// damit im Tages-Drawdown enthalten; vom Einstieg aus zu rechnen würde ihn doppelt zählen.
/// Steht der Stop bereits im Gewinn, ist das offene Risiko null - nicht negativ.
/// </remarks>
public readonly struct OpenRiskItem
{
    public OpenRiskItem(Position position, decimal currentPrice, decimal valuePerPricePointPerUnit)
    {
        if (position == null)
        {
            throw new ArgumentNullException(nameof(position));
        }

        if (valuePerPricePointPerUnit <= 0m)
        {
            throw new ArgumentOutOfRangeException(
                nameof(valuePerPricePointPerUnit), valuePerPricePointPerUnit, "Punktwert muss positiv sein.");
        }

        Position = position;
        CurrentPrice = currentPrice;
        ValuePerPricePointPerUnit = valuePerPricePointPerUnit;
    }

    public Position Position { get; }

    public decimal CurrentPrice { get; }

    public decimal ValuePerPricePointPerUnit { get; }

    /// <summary>Verlust in Kontowährung, falls der Stop dieser Position jetzt auslöst. Nie negativ.</summary>
    public decimal RiskAmount
    {
        get
        {
            var distance = (CurrentPrice - Position.StopLoss) * Position.Direction.Sign();
            return distance <= 0m ? 0m : distance * Position.Quantity * ValuePerPricePointPerUnit;
        }
    }
}

/// <summary>Aggregation offener Risiken über beliebig viele Positionen und Symbole.</summary>
public static class OpenRisk
{
    public static decimal TotalAmount(IReadOnlyList<OpenRiskItem> items)
    {
        if (items == null)
        {
            return 0m;
        }

        var total = 0m;
        for (var i = 0; i < items.Count; i++)
        {
            total += items[i].RiskAmount;
        }

        return total;
    }

    /// <summary>Offenes Risiko in Prozent der angegebenen Bezugsgröße (Kontostand bei Tagesbeginn).</summary>
    public static decimal TotalPercent(IReadOnlyList<OpenRiskItem> items, decimal basis)
    {
        if (basis <= 0m)
        {
            return 0m;
        }

        return TotalAmount(items) / basis * 100m;
    }
}
