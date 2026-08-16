using System;
using System.Globalization;

namespace Daytrading.Strategies.Model;

/// <summary>Art des Signals.</summary>
public enum SignalKind
{
    /// <summary>Einstieg in eine neue Position.</summary>
    Entry = 0,

    /// <summary>
    /// Bitte offene Positionen dieser Strategie auf diesem Symbol schließen.
    /// Für Strategien, die selbst aussteigen wollen (z.B. Rückkehr zum Mittelwert),
    /// bevor Stop oder Ziel erreicht sind. Zwangsschließung vor Sessionende und
    /// maximale Haltedauer laufen dagegen in der Ausführungsschicht.
    /// </summary>
    CloseAll = 1,
}

/// <summary>
/// Die einzige Ausgabe einer Strategie: eine Handelsabsicht, keine Order.
/// </summary>
/// <remarks>
/// Der Stop-Loss ist ein Pflichtfeld des Konstruktors und keine Option. Ein Signal ohne
/// Stop kann damit gar nicht erst entstehen - die Ausführungsschicht muss nur noch
/// unplausible Stops abweisen, nicht fehlende ergänzen.
///
/// <see cref="RiskPercent"/> ist ein <em>Wunsch</em> der Strategie, keine Zusage. Die
/// Ausführungsschicht deckelt ihn an ihren eigenen Grenzen (Tagesverlust, Drawdown,
/// Zustand aus der Bewertungsschicht) und rechnet daraus erst die Positionsgröße.
///
/// Preise beziehen sich auf <see cref="ReferencePrice"/>, üblicherweise den Close der
/// Signalbar. Ausgeführt wird frühestens auf der Open der Folgebar; die tatsächliche
/// Einstiegsdifferenz ist Teil des Slippage-Modells und wird protokolliert.
/// </remarks>
public sealed class Signal
{
    private Signal(
        SignalKind kind,
        TradeDirection? direction,
        decimal referencePrice,
        decimal? stopLoss,
        decimal? takeProfit,
        decimal? riskPercent,
        string reason)
    {
        Kind = kind;
        Direction = direction;
        ReferencePrice = referencePrice;
        StopLoss = stopLoss;
        TakeProfit = takeProfit;
        RiskPercent = riskPercent;
        Reason = reason;
    }

    public SignalKind Kind { get; }

    /// <summary>Richtung des Einstiegs. Bei <see cref="SignalKind.CloseAll"/> null.</summary>
    public TradeDirection? Direction { get; }

    /// <summary>Preis, auf den sich Stop und Ziel beziehen - in der Regel der Close der Signalbar.</summary>
    public decimal ReferencePrice { get; }

    /// <summary>Stop-Loss als absoluter Preis. Bei Einstiegssignalen immer gesetzt.</summary>
    public decimal? StopLoss { get; }

    /// <summary>Optionales Kursziel als absoluter Preis.</summary>
    public decimal? TakeProfit { get; }

    /// <summary>Gewünschtes Risiko in Prozent des Kontostands. Null bedeutet: Default der Ausführungsschicht.</summary>
    public decimal? RiskPercent { get; }

    /// <summary>Kurzbegründung für Trade-Log und Regime-Auswertung, z.B. "ORB long break 30m".</summary>
    public string Reason { get; }

    /// <summary>Preisabstand zwischen Referenzpreis und Stop.</summary>
    public decimal StopDistance => StopLoss.HasValue ? Math.Abs(ReferencePrice - StopLoss.Value) : 0m;

    /// <summary>Chance-Risiko-Verhältnis, sofern ein Ziel gesetzt ist.</summary>
    public decimal? RewardRiskRatio
    {
        get
        {
            if (!TakeProfit.HasValue || StopDistance <= 0m)
            {
                return null;
            }

            return Math.Abs(TakeProfit.Value - ReferencePrice) / StopDistance;
        }
    }

    /// <summary>
    /// Erzeugt ein Einstiegssignal und prüft dabei die Invarianten, die eine Strategie
    /// nicht verletzen darf.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// Wenn der Stop auf der falschen Seite liegt, der Stopabstand null ist oder das Ziel
    /// nicht in Richtung des Trades liegt. Das ist ein Fehler in der Strategie, kein
    /// Marktzustand - deshalb eine Ausnahme und kein stilles Verwerfen.
    /// </exception>
    public static Signal Entry(
        TradeDirection direction,
        decimal referencePrice,
        decimal stopLoss,
        decimal? takeProfit = null,
        decimal? riskPercent = null,
        string reason = "")
    {
        var sign = direction.Sign();

        if ((referencePrice - stopLoss) * sign <= 0m)
        {
            throw new ArgumentException(
                string.Format(
                    CultureInfo.InvariantCulture,
                    "Ungültiger Stop-Loss: {0}-Einstieg bei {1} mit Stop {2}. Der Stop muss {3} des Einstiegs liegen " +
                    "und einen Abstand größer null haben.",
                    direction, referencePrice, stopLoss, direction == TradeDirection.Long ? "unterhalb" : "oberhalb"),
                nameof(stopLoss));
        }

        if (takeProfit.HasValue && (takeProfit.Value - referencePrice) * sign <= 0m)
        {
            throw new ArgumentException(
                string.Format(
                    CultureInfo.InvariantCulture,
                    "Ungültiges Kursziel: {0}-Einstieg bei {1} mit Ziel {2}.",
                    direction, referencePrice, takeProfit.Value),
                nameof(takeProfit));
        }

        if (riskPercent.HasValue && (riskPercent.Value <= 0m || riskPercent.Value > 100m))
        {
            throw new ArgumentOutOfRangeException(
                nameof(riskPercent), riskPercent.Value, "Risiko in Prozent muss zwischen 0 (exklusiv) und 100 liegen.");
        }

        return new Signal(SignalKind.Entry, direction, referencePrice, stopLoss, takeProfit, riskPercent, reason ?? string.Empty);
    }

    /// <summary>Bittet die Ausführungsschicht, offene Positionen dieser Strategie zu schließen.</summary>
    public static Signal CloseAll(decimal referencePrice, string reason = "") =>
        new Signal(SignalKind.CloseAll, null, referencePrice, null, null, null, reason ?? string.Empty);

    public override string ToString() =>
        Kind == SignalKind.CloseAll
            ? string.Format(CultureInfo.InvariantCulture, "CloseAll @{0} ({1})", ReferencePrice, Reason)
            : string.Format(
                CultureInfo.InvariantCulture,
                "{0} @{1} SL={2} TP={3} Risk={4} ({5})",
                Direction, ReferencePrice, StopLoss,
                TakeProfit.HasValue ? TakeProfit.Value.ToString(CultureInfo.InvariantCulture) : "-",
                RiskPercent.HasValue ? RiskPercent.Value.ToString(CultureInfo.InvariantCulture) + "%" : "default",
                Reason);
}
