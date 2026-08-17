using System;
using Daytrading.Strategies.Model;

namespace Daytrading.Strategies;

/// <summary>
/// Dreht die Signale einer Strategie um: Aus Long wird Short, aus Short wird Long.
/// </summary>
/// <remarks>
/// Die naheliegende Idee, wenn eine Strategie verlässlich verliert. Sie hält aber nur, wenn
/// das Verlieren aus der <b>Richtung</b> kommt. Kommt es aus den <b>Kosten</b>, ändert das
/// Umdrehen nichts: Spread, Kommission und Slippage werden in beide Richtungen bezahlt. Aus
/// −0.3 R wird dann nicht +0.3 R, sondern wieder etwas Negatives.
///
/// Genau deshalb steht diese Klasse hier: Der Unterschied ist messbar, und die Messung
/// entscheidet, statt der Vermutung. Wird die gespiegelte Fassung deutlich positiv, lag es an
/// der Richtung. Bleibt sie negativ, sind es die Kosten - und dann hilft nur ein echter
/// Vorteil, keine Vorzeichenänderung.
///
/// Gespiegelt wird am Referenzpreis, nicht am Vorzeichen allein: Ein Long bei 100 mit Stop 99
/// und Ziel 102 wird zu einem Short bei 100 mit Stop 101 und Ziel 98. Nur so bleiben
/// Stopabstand und Chance-Risiko-Verhältnis erhalten und die beiden Läufe vergleichbar.
/// </remarks>
public sealed class InvertedStrategy : IStrategy
{
    private readonly IStrategy _inner;

    public InvertedStrategy(IStrategy inner)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        Descriptor = new StrategyDescriptor(
            "Inverted" + _inner.Descriptor.Name,
            _inner.Descriptor.Version);
    }

    public StrategyDescriptor Descriptor { get; }

    public int WarmupBars => _inner.WarmupBars;

    public void Initialize(StrategyContext context) => _inner.Initialize(context);

    public Signal? OnBar(MarketSnapshot snapshot)
    {
        var signal = _inner.OnBar(snapshot);

        // Ausstiegssignale bleiben Ausstiegssignale - "schließen" hat keine Gegenrichtung.
        if (signal == null || signal.Kind != SignalKind.Entry || signal.Direction == null)
        {
            return signal;
        }

        var reference = signal.ReferencePrice;
        var flipped = signal.Direction.Value == TradeDirection.Long
            ? TradeDirection.Short
            : TradeDirection.Long;

        return Signal.Entry(
            flipped,
            reference,
            Mirror(reference, signal.StopLoss!.Value),
            signal.TakeProfit.HasValue ? Mirror(reference, signal.TakeProfit.Value) : (decimal?)null,
            signal.RiskPercent,
            "invers: " + signal.Reason);
    }

    /// <summary>Spiegelt einen Preis am Referenzpreis: aus 99 wird bei Referenz 100 der Wert 101.</summary>
    private static decimal Mirror(decimal reference, decimal price) => reference + (reference - price);
}
