using System;
using System.Collections.Generic;
using Daytrading.Strategies;
using Daytrading.Strategies.Model;

namespace Daytrading.Strategies.Tests.TestSupport;

/// <summary>Ein von der Strategie erzeugtes Signal samt Zeitpunkt.</summary>
internal sealed record SignalRecord(DateTime BarCloseUtc, Signal Signal);

/// <summary>
/// Minimaler Host für Strategietests: spielt Bars chronologisch ein, baut je Bar einen
/// <see cref="MarketSnapshot"/> und sammelt die Signale.
/// </summary>
/// <remarks>
/// Das ist genau die Rolle, die später Backtester und cBot übernehmen - und der Beleg,
/// dass Strategien ohne cTrader vollständig testbar sind. Ausführung, Kosten und
/// Risikoregeln fehlen hier absichtlich; die gehören in die Ausführungsschicht und
/// bekommen eigene Tests.
/// </remarks>
internal sealed class StrategyHarness
{
    private readonly IStrategy _strategy;
    private readonly SymbolInfo _symbol;
    private readonly Timeframe _timeframe;
    private readonly Func<DateTime, TradingSession> _sessionResolver;
    private readonly BarSeries _history = new BarSeries();

    public StrategyHarness(
        IStrategy strategy,
        SymbolInfo symbol,
        Timeframe timeframe,
        Func<DateTime, TradingSession> sessionResolver,
        IEnumerable<KeyValuePair<string, string>>? parameters = null)
    {
        _strategy = strategy;
        _symbol = symbol;
        _timeframe = timeframe;
        _sessionResolver = sessionResolver;

        Parameters = new StrategyParameters(parameters);
        _strategy.Initialize(new StrategyContext(symbol, timeframe, Parameters, Log));
    }

    public StrategyParameters Parameters { get; }

    public InMemoryStrategyLog Log { get; } = new InMemoryStrategyLog();

    /// <summary>Offene Positionen, die der Strategie im Snapshot gezeigt werden.</summary>
    public List<Position> OpenPositions { get; } = new List<Position>();

    public List<SignalRecord> Signals { get; } = new List<SignalRecord>();

    public IBarSeries History => _history;

    public IReadOnlyList<SignalRecord> Run(IEnumerable<Candle> candles)
    {
        foreach (var candle in candles)
        {
            RunSingle(candle);
        }

        return Signals;
    }

    public Signal? RunSingle(Candle candle)
    {
        _history.Append(candle);
        var session = _sessionResolver(candle.OpenTimeUtc);
        var snapshot = new MarketSnapshot(_symbol, _timeframe, _history, session, OpenPositions);

        var signal = _strategy.OnBar(snapshot);
        if (signal != null)
        {
            Signals.Add(new SignalRecord(snapshot.BarCloseTimeUtc, signal));
        }

        return signal;
    }
}
