using System;

namespace Daytrading.Strategies.Model;

/// <summary>
/// Eine offene Position, wie Backtester und cBot sie führen.
/// </summary>
/// <remarks>
/// Eine Strategie sieht offene Positionen über <see cref="MarketSnapshot.OpenPositions"/>,
/// damit sie weiß, ob sie bereits im Markt ist. Sie soll dabei ausschließlich Richtung,
/// Einstiegszeit und Preise auswerten - <see cref="Quantity"/> gehört der Ausführungsschicht.
/// Positionsgröße ist Ergebnis der Risikorechnung, nicht Eingabe der Strategie.
/// </remarks>
public sealed class Position
{
    public Position(
        string id,
        string symbol,
        TradeDirection direction,
        DateTime entryTimeUtc,
        decimal entryPrice,
        decimal stopLoss,
        decimal? takeProfit,
        decimal quantity,
        string strategyTag)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            throw new ArgumentException("Positions-Id darf nicht leer sein.", nameof(id));
        }

        if (string.IsNullOrWhiteSpace(symbol))
        {
            throw new ArgumentException("Symbol darf nicht leer sein.", nameof(symbol));
        }

        if (entryTimeUtc.Kind != DateTimeKind.Utc)
        {
            throw new ArgumentException("Einstiegszeit muss UTC sein.", nameof(entryTimeUtc));
        }

        if (quantity <= 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(quantity), quantity, "Positionsgröße muss positiv sein.");
        }

        Id = id;
        Symbol = symbol;
        Direction = direction;
        EntryTimeUtc = entryTimeUtc;
        EntryPrice = entryPrice;
        StopLoss = stopLoss;
        TakeProfit = takeProfit;
        Quantity = quantity;
        StrategyTag = strategyTag ?? string.Empty;
    }

    public string Id { get; }

    public string Symbol { get; }

    public TradeDirection Direction { get; }

    public DateTime EntryTimeUtc { get; }

    public decimal EntryPrice { get; }

    /// <summary>Stop-Loss der Position. Pflicht - eine Position ohne Stop entsteht im Framework nicht.</summary>
    public decimal StopLoss { get; }

    public decimal? TakeProfit { get; }

    /// <summary>Positionsgröße in Instrumenteinheiten. Von der Ausführungsschicht gesetzt.</summary>
    public decimal Quantity { get; }

    /// <summary>Welche Strategie-Instanz die Position eröffnet hat - für getrennte Trade-Logs je Konto.</summary>
    public string StrategyTag { get; }

    /// <summary>Preisabstand zwischen Einstieg und Stop.</summary>
    public decimal RiskDistance => Math.Abs(EntryPrice - StopLoss);

    /// <summary>Haltedauer bis zum angegebenen Zeitpunkt.</summary>
    public TimeSpan HoldingTime(DateTime utcNow) => utcNow - EntryTimeUtc;

    public override string ToString() =>
        $"{Symbol} {Direction} @{EntryPrice} SL={StopLoss} TP={(TakeProfit.HasValue ? TakeProfit.Value.ToString() : "-")} " +
        $"Qty={Quantity} seit {EntryTimeUtc:yyyy-MM-dd HH:mm}";
}
