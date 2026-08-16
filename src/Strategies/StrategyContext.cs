using System;
using Daytrading.Strategies.Model;

namespace Daytrading.Strategies;

/// <summary>
/// Umgebung, die eine Strategie bei der Initialisierung bekommt.
/// </summary>
/// <remarks>
/// Enthält Symbol, Timeframe, Parameter, Log und einen festen Zufallsseed - und bewusst
/// keinen Kontostand, keine Kosten, keine Broker-Verbindung. Alles, was hier fehlt, ist
/// genau das, was Strategien nicht wissen dürfen.
/// </remarks>
public sealed class StrategyContext
{
    public StrategyContext(
        SymbolInfo symbol,
        Timeframe timeframe,
        StrategyParameters? parameters = null,
        IStrategyLog? log = null,
        int randomSeed = 0)
    {
        Symbol = symbol ?? throw new ArgumentNullException(nameof(symbol));
        Timeframe = timeframe;
        Parameters = parameters ?? StrategyParameters.Empty;
        Log = log ?? NullStrategyLog.Instance;
        RandomSeed = randomSeed;
    }

    public SymbolInfo Symbol { get; }

    public Timeframe Timeframe { get; }

    public StrategyParameters Parameters { get; }

    public IStrategyLog Log { get; }

    /// <summary>
    /// Fester Seed für jede Strategie, die Zufall braucht. Backtests müssen reproduzierbar
    /// sein - ein selbst erzeugter <see cref="Random"/> ohne Seed wäre es nicht.
    /// </summary>
    public int RandomSeed { get; }
}
