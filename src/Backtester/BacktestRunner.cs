using System;
using System.Collections.Generic;
using System.Globalization;
using Daytrading.Data.Config;
using Daytrading.Execution;
using Daytrading.Strategies;
using Daytrading.Strategies.Model;

namespace Daytrading.Backtester;

/// <summary>Ein auszuführender Backtest.</summary>
public sealed class BacktestJob
{
    public BacktestJob(
        IStrategy strategy,
        StrategyParameters parameters,
        SymbolConfig symbol,
        IReadOnlyList<Candle> bars,
        RiskLimits limits,
        BacktestOptions options)
    {
        Strategy = strategy ?? throw new ArgumentNullException(nameof(strategy));
        Parameters = parameters ?? StrategyParameters.Empty;
        Symbol = symbol ?? throw new ArgumentNullException(nameof(symbol));
        Bars = bars ?? throw new ArgumentNullException(nameof(bars));
        Limits = limits ?? throw new ArgumentNullException(nameof(limits));
        Options = options ?? new BacktestOptions();
    }

    public IStrategy Strategy { get; }

    public StrategyParameters Parameters { get; }

    public SymbolConfig Symbol { get; }

    public IReadOnlyList<Candle> Bars { get; }

    public RiskLimits Limits { get; }

    public BacktestOptions Options { get; }
}

/// <summary>
/// Spielt Bars gegen Strategie, Risikoschicht und Kostenmodell.
/// </summary>
/// <remarks>
/// Ausführungsregeln, bewusst so und nicht anders:
/// <list type="bullet">
///   <item><b>Einstiege frühestens auf der Open der Folgebar.</b> Ein Signal entsteht auf dem
///         Close der Signalbar; auf demselben Close auszuführen wäre Look-ahead.</item>
///   <item><b>Zeitgesteuerte Ausstiege auf dem Close der laufenden Bar.</b> Zwangsschließung vor
///         Sessionende und maximale Haltedauer hängen nicht vom Kurs ab, verschaffen also keinen
///         Informationsvorteil - und aufgeschoben würden sie die Regel "kein Overnight" brechen.</item>
///   <item><b>Stop und Ziel innerhalb der Bar.</b> Können beide getroffen worden sein, gilt der
///         Stop als zuerst erreicht. Der tatsächliche Kursverlauf innerhalb der Bar ist unbekannt;
///         die pessimistische Annahme ist die einzige, die nicht schönrechnet.</item>
///   <item><b>Kurslücken über den Stop hinweg werden zum Eröffnungskurs gefüllt</b>, nicht zum
///         Stop-Preis. Ein Stop ist keine Garantie.</item>
/// </list>
/// Der Lauf ist deterministisch: gleiche Bars, gleiche Parameter, gleiches Ergebnis.
/// </remarks>
public sealed class BacktestRunner
{
    private sealed class OpenTrade
    {
        public OpenTrade(Position position, decimal riskAmount, decimal riskPercent, int entryBarIndex)
        {
            Position = position;
            RiskAmount = riskAmount;
            RiskPercent = riskPercent;
            EntryBarIndex = entryBarIndex;
        }

        public Position Position { get; }

        public decimal RiskAmount { get; }

        public decimal RiskPercent { get; }

        public int EntryBarIndex { get; }
    }

    public BacktestResult Run(BacktestJob job)
    {
        if (job == null)
        {
            throw new ArgumentNullException(nameof(job));
        }

        var config = job.Symbol;
        config.Validate();

        var symbolInfo = config.ToSymbolInfo();
        var timeframe = config.ToTimeframe();
        var executionSymbol = new ExecutionSymbol(
            symbolInfo,
            config.MinQuantity,
            config.QuantityStep,
            config.MaxQuantity,
            config.ValuePerPricePointPerUnit,
            config.TypicalSpread,
            config.CommissionPerUnitPerSide);

        var calendar = new SymbolSessionCalendar(config);
        var cost = new CostModel(config, job.Options.SlippageTicks, job.Options.StopSlippageTicks);
        var log = new InMemoryStrategyLog();
        var engine = new ExecutionEngine(executionSymbol, job.Limits, log);
        var account = new AccountState(job.Options.StartingBalance);

        job.Strategy.Initialize(new StrategyContext(symbolInfo, timeframe, job.Parameters, log, job.Options.RandomSeed));

        var history = new BarSeries(job.Bars.Count);
        var openTrades = new List<OpenTrade>();
        var positions = new List<Position>();
        var pendingEntries = new List<ExecutionInstruction>();
        var trades = new List<TradeRecord>();
        var equity = new List<EquityPoint>(job.Bars.Count);
        var rejections = new Dictionary<RiskRejectionReason, int>();
        var warnings = new List<string>();

        var strategyKey = job.Strategy.Descriptor.Key;
        var positionCounter = 0;
        var barsProcessed = 0;
        var barsOutside = 0;
        DateTime? stoppedAt = null;
        string? stopReason = null;
        DateTime? firstBarUtc = null;
        var lastBarUtc = DateTime.MinValue;

        for (var index = 0; index < job.Bars.Count; index++)
        {
            var bar = job.Bars[index];
            var barClose = bar.OpenTimeUtc + timeframe.Duration;

            if (!calendar.TryGetSessionAt(barClose, out var session))
            {
                barsOutside++;
                continue;
            }

            barsProcessed++;
            firstBarUtc ??= bar.OpenTimeUtc;
            lastBarUtc = barClose;
            history.Append(bar);

            var thinAtOpen = CostModel.IsThinLiquidity(session, bar.OpenTimeUtc);
            var thinAtClose = CostModel.IsThinLiquidity(session, barClose);

            // A) Aufträge der Vorbar auf der Open ausführen.
            foreach (var instruction in pendingEntries)
            {
                var direction = instruction.Direction!.Value;
                var entryPrice = cost.EntryPrice(direction, bar.Open, thinAtOpen);
                var stop = instruction.StopLoss!.Value;

                if ((entryPrice - stop) * direction.Sign() <= 0m)
                {
                    // Die Kurslücke hat den Stop bereits überholt - der Einstieg wäre sofort
                    // ausgestoppt. Solche Fills werden verworfen und gezählt.
                    warnings.Add(string.Format(
                        CultureInfo.InvariantCulture,
                        "{0:yyyy-MM-dd HH:mm}: Einstieg verworfen, Eröffnungskurs {1} liegt bereits jenseits des Stops {2}.",
                        bar.OpenTimeUtc, entryPrice, stop));
                    continue;
                }

                var position = new Position(
                    "T" + (++positionCounter).ToString(CultureInfo.InvariantCulture),
                    config.Name,
                    direction,
                    bar.OpenTimeUtc,
                    entryPrice,
                    stop,
                    instruction.TakeProfit,
                    instruction.Quantity,
                    strategyKey);

                // Risiko aus dem tatsächlichen Fill, nicht aus dem geplanten Preis: nur so ist
                // das R-Vielfache im Trade-Log ehrlich.
                var risk = Math.Abs(entryPrice - stop) * instruction.Quantity * config.ValuePerPricePointPerUnit;
                openTrades.Add(new OpenTrade(position, risk, instruction.RiskPercent, index));
                positions.Add(position);
                account.ApplyRealizedPnL(-cost.Commission(instruction.Quantity));
            }

            pendingEntries.Clear();

            // B) Stop und Ziel innerhalb der Bar.
            for (var i = openTrades.Count - 1; i >= 0; i--)
            {
                var trade = openTrades[i];
                var exit = DetectIntrabarExit(trade, bar, index);
                if (exit == null)
                {
                    continue;
                }

                CloseTrade(
                    trades, openTrades, positions, account, cost, config, i, exit.Value.Price, barClose,
                    exit.Value.Reason, exit.Value.Kind, thinAtClose, strategyKey);
            }

            // C) Bewertung zum Bar-Close.
            account.UpdateEquity(account.Balance + Unrealized(openTrades, bar.Close, config));
            equity.Add(new EquityPoint(barClose, account.Equity));

            // D) Strategie und Risikoschicht.
            var snapshot = new MarketSnapshot(symbolInfo, timeframe, history, session, positions);
            var signal = job.Strategy.OnBar(snapshot);
            var instructions = engine.OnBar(snapshot, account, signal);

            if (signal is { Kind: SignalKind.Entry } && engine.LastDecision is { Accepted: false } decision)
            {
                rejections.TryGetValue(decision.Reason, out var count);
                rejections[decision.Reason] = count + 1;
            }

            // Der Gesamt-Drawdown ist erreicht: Nach den Regeln wird die Strategie gestoppt und
            // geprueft. Also endet auch der Lauf hier, statt jahrelang ohne Trades weiterzuzaehlen.
            if (account.TotalDrawdownPercent >= job.Limits.MaxTotalDrawdownPercent)
            {
                stoppedAt = barClose;
                stopReason = string.Format(
                    CultureInfo.InvariantCulture,
                    "Gesamt-Drawdown {0:0.00} % erreicht die Grenze von {1} %.",
                    account.TotalDrawdownPercent, job.Limits.MaxTotalDrawdownPercent);
                warnings.Add(string.Format(
                    CultureInfo.InvariantCulture,
                    "{0:yyyy-MM-dd HH:mm}: Lauf beendet - {1}", barClose, stopReason));
                break;
            }

            // E) Anweisungen umsetzen.
            foreach (var instruction in instructions)
            {
                if (instruction.Kind == ExecutionInstructionKind.OpenPosition)
                {
                    pendingEntries.Add(instruction);
                    continue;
                }

                var i = openTrades.FindIndex(trade => trade.Position.Id == instruction.PositionId);
                if (i < 0)
                {
                    continue;
                }

                CloseTrade(
                    trades, openTrades, positions, account, cost, config, i, bar.Close, barClose,
                    instruction.ExitReason ?? ExitReason.RiskLimit, FillKind.Market, thinAtClose, strategyKey);
            }

            account.UpdateEquity(account.Balance + Unrealized(openTrades, bar.Close, config));
        }

        // Offene Positionen am Ende schließen - im Report als Sonderfall gekennzeichnet.
        // Wurde der Lauf durch eine Risikogrenze beendet, ist das der Grund, nicht das Datenende.
        if (openTrades.Count > 0 && history.Count > 0)
        {
            var last = history.Last();
            var lastClose = last.OpenTimeUtc + timeframe.Duration;
            var reason = stoppedAt.HasValue ? ExitReason.RiskLimit : ExitReason.EndOfData;
            for (var i = openTrades.Count - 1; i >= 0; i--)
            {
                CloseTrade(
                    trades, openTrades, positions, account, cost, config, i, last.Close, lastClose,
                    reason, FillKind.Market, false, strategyKey);
            }

            account.UpdateEquity(account.Balance);
            equity.Add(new EquityPoint(lastClose, account.Equity));
        }

        foreach (var entry in log.Entries)
        {
            if (entry.StartsWith("[Error]", StringComparison.Ordinal))
            {
                warnings.Add(entry);
            }
        }

        return new BacktestResult
        {
            StrategyKey = strategyKey,
            ParameterFingerprint = job.Parameters.Fingerprint,
            Symbol = config.Name,
            Timeframe = timeframe,
            FromUtc = job.Bars.Count > 0 ? job.Bars[0].OpenTimeUtc : DateTime.MinValue,
            ToUtc = job.Bars.Count > 0 ? job.Bars[^1].OpenTimeUtc : DateTime.MinValue,
            StartingBalance = job.Options.StartingBalance,
            FinalBalance = account.Balance,
            Trades = trades,
            EquityCurve = equity,
            Metrics = PerformanceMetrics.Compute(trades, equity, job.Options.StartingBalance, job.Options.MinimumSampleTrades),
            Rejections = rejections,
            StoppedAtUtc = stoppedAt,
            StopReason = stopReason,
            ActiveDays = firstBarUtc.HasValue ? (int)Math.Ceiling((lastBarUtc - firstBarUtc.Value).TotalDays) : 0,
            BarsProcessed = barsProcessed,
            BarsOutsideSession = barsOutside,
            Warnings = warnings,
        };
    }

    private static (decimal Price, ExitReason Reason, FillKind Kind)? DetectIntrabarExit(OpenTrade trade, Candle bar, int index)
    {
        var position = trade.Position;
        var openedThisBar = trade.EntryBarIndex == index;
        var isLong = position.Direction == TradeDirection.Long;

        var stopHit = isLong ? bar.Low <= position.StopLoss : bar.High >= position.StopLoss;
        var targetHit = position.TakeProfit.HasValue
            && (isLong ? bar.High >= position.TakeProfit.Value : bar.Low <= position.TakeProfit.Value);

        if (stopHit)
        {
            // Pessimistisch: Wurden Stop und Ziel beide berührt, gilt der Stop zuerst.
            var price = position.StopLoss;
            if (!openedThisBar)
            {
                // Kurslücke über den Stop hinweg: gefüllt wird zum Eröffnungskurs, nicht zum Stop.
                price = isLong ? Math.Min(price, bar.Open) : Math.Max(price, bar.Open);
            }

            return (price, ExitReason.StopLoss, FillKind.Stop);
        }

        if (targetHit)
        {
            var price = position.TakeProfit!.Value;
            if (!openedThisBar)
            {
                price = isLong ? Math.Max(price, bar.Open) : Math.Min(price, bar.Open);
            }

            return (price, ExitReason.TakeProfit, FillKind.Limit);
        }

        return null;
    }

    private static void CloseTrade(
        List<TradeRecord> trades,
        List<OpenTrade> openTrades,
        List<Position> positions,
        AccountState account,
        CostModel cost,
        SymbolConfig config,
        int index,
        decimal exitMidPrice,
        DateTime exitTimeUtc,
        ExitReason reason,
        FillKind kind,
        bool thinLiquidity,
        string strategyKey)
    {
        var trade = openTrades[index];
        var position = trade.Position;
        var exitPrice = cost.ExitPrice(position.Direction, exitMidPrice, thinLiquidity, kind);

        var gross = (exitPrice - position.EntryPrice)
                    * position.Direction.Sign()
                    * position.Quantity
                    * config.ValuePerPricePointPerUnit;

        // Die Einstiegskommission wurde beim Öffnen gebucht, hier folgt die zweite Seite.
        var commission = cost.Commission(position.Quantity);
        account.ApplyRealizedPnL(gross - commission);

        trades.Add(new TradeRecord(
            strategyKey,
            position,
            exitTimeUtc,
            exitPrice,
            reason,
            gross,
            cost.RoundTripCommission(position.Quantity),
            trade.RiskAmount,
            trade.RiskPercent));

        openTrades.RemoveAt(index);
        positions.Remove(position);
    }

    private static decimal Unrealized(List<OpenTrade> openTrades, decimal price, SymbolConfig config)
    {
        var total = 0m;
        foreach (var trade in openTrades)
        {
            var position = trade.Position;
            total += (price - position.EntryPrice)
                     * position.Direction.Sign()
                     * position.Quantity
                     * config.ValuePerPricePointPerUnit;
        }

        return total;
    }
}
