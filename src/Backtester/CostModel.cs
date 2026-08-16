using System;
using Daytrading.Data.Config;
using Daytrading.Strategies.Model;

namespace Daytrading.Backtester;

/// <summary>Wie eine Order zustande kommt - davon hängt ab, welche Kosten realistisch sind.</summary>
public enum FillKind
{
    /// <summary>Marktorder: Spread und Slippage.</summary>
    Market = 0,

    /// <summary>Stop-Order: Spread und erhöhte Slippage, weil sie in die Bewegung hinein ausgelöst wird.</summary>
    Stop = 1,

    /// <summary>Limit-Order: Spread, aber keine negative Slippage - man bekommt den Preis oder gar nichts.</summary>
    Limit = 2,
}

/// <summary>
/// Kostenmodell des Backtests: Spread, Slippage, Kommission.
/// </summary>
/// <remarks>
/// Ohne diese drei Größen sind Backtest-Ergebnisse im Daytrading wertlos - bei kurzen
/// Haltedauern und engen Zielen entscheiden sie über Gewinn und Verlust.
///
/// Modellierung:
/// <list type="bullet">
///   <item>Kursdaten gelten als Mittelkurs. Gekauft wird zum Brief (Mid + halber Spread),
///         verkauft zum Geld (Mid − halber Spread). Ein Hin und Zurück kostet also einen
///         vollen Spread.</item>
///   <item>In dünnen Phasen - hier die Randzeiten der Session - wird der Spread mit dem
///         konfigurierten Faktor multipliziert.</item>
///   <item>Slippage wirkt immer gegen die Position. Stops bekommen mehr davon als
///         Marktorders, Limits gar keine.</item>
///   <item>Swap wird bewusst ignoriert: Bei reinem Daytrading wird keine Position über
///         Nacht gehalten, es fällt also keiner an. Diese Annahme ist Teil der Regeln
///         und nicht bloß eine Vereinfachung.</item>
/// </list>
/// </remarks>
public sealed class CostModel
{
    private readonly decimal _spread;
    private readonly decimal _thinFactor;
    private readonly decimal _tickSize;
    private readonly decimal _commissionPerUnit;
    private readonly decimal _slippageTicks;
    private readonly decimal _stopSlippageTicks;

    public CostModel(SymbolConfig config, decimal slippageTicks = 1m, decimal stopSlippageTicks = 3m)
    {
        if (config == null)
        {
            throw new ArgumentNullException(nameof(config));
        }

        if (slippageTicks < 0m || stopSlippageTicks < 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(slippageTicks), "Slippage darf nicht negativ sein.");
        }

        _spread = config.TypicalSpread;
        _thinFactor = config.ThinLiquiditySpreadFactor;
        _tickSize = config.TickSize;
        _commissionPerUnit = config.CommissionPerUnitPerSide;
        _slippageTicks = slippageTicks;
        _stopSlippageTicks = stopSlippageTicks;
    }

    /// <summary>Halber Spread zum angegebenen Zeitpunkt der Session.</summary>
    public decimal HalfSpread(bool thinLiquidity) => _spread / 2m * (thinLiquidity ? _thinFactor : 1m);

    /// <summary>Ausführungspreis beim Einstieg. Immer schlechter als der Mittelkurs.</summary>
    public decimal EntryPrice(TradeDirection direction, decimal midPrice, bool thinLiquidity, FillKind kind = FillKind.Market) =>
        midPrice + direction.Sign() * (HalfSpread(thinLiquidity) + Slippage(kind));

    /// <summary>Ausführungspreis beim Ausstieg. Ebenfalls immer schlechter als der Mittelkurs.</summary>
    public decimal ExitPrice(TradeDirection direction, decimal midPrice, bool thinLiquidity, FillKind kind = FillKind.Market) =>
        midPrice - direction.Sign() * (HalfSpread(thinLiquidity) + Slippage(kind));

    /// <summary>Kommission einer Handelsrichtung.</summary>
    public decimal Commission(decimal quantity) => _commissionPerUnit * quantity;

    /// <summary>Kommission für Ein- und Ausstieg zusammen.</summary>
    public decimal RoundTripCommission(decimal quantity) => 2m * Commission(quantity);

    private decimal Slippage(FillKind kind) => kind switch
    {
        FillKind.Limit => 0m,
        FillKind.Stop => _stopSlippageTicks * _tickSize,
        _ => _slippageTicks * _tickSize,
    };

    /// <summary>
    /// Grober Näherungswert für dünne Liquidität: die ersten und letzten zehn Prozent der Session.
    /// Bewusst einfach gehalten und im Report als Annahme ausgewiesen.
    /// </summary>
    public static bool IsThinLiquidity(TradingSession session, DateTime utc)
    {
        var length = session.Length;
        if (length <= TimeSpan.Zero)
        {
            return false;
        }

        var margin = TimeSpan.FromTicks(length.Ticks / 10);
        return utc < session.StartUtc + margin || utc > session.EndUtc - margin;
    }
}
