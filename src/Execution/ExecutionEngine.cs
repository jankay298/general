using System;
using System.Collections.Generic;
using Daytrading.Strategies;
using Daytrading.Strategies.Model;

namespace Daytrading.Execution;

/// <summary>
/// Verbindet Strategie-Signal, Tagesregeln und Risikoprüfung zu einer Liste von Anweisungen.
/// </summary>
/// <remarks>
/// Reine Entscheidungsschicht ohne Seiteneffekte auf den Markt: Sie öffnet und schließt nichts
/// selbst, sondern sagt dem Host, was zu tun ist. Backtester und cBot führen dieselben
/// Anweisungen aus - der eine gegen ein Kostenmodell, der andere gegen cTrader.
///
/// Reihenfolge je Bar, und zwar in dieser Reihenfolge aus gutem Grund:
/// <list type="number">
///   <item>Handelstag fortschreiben (Tagesgrenzen und Trade-Zähler zurücksetzen)</item>
///   <item>Ausstiege aus den Tagesregeln (Sessionende, Haltedauer, Overnight)</item>
///   <item>Ausstiegswunsch der Strategie</item>
///   <item>Erst danach ein neuer Einstieg, geprüft durch das <see cref="RiskGate"/></item>
/// </list>
///
/// Stop- und Zieltreffer erzeugt diese Schicht nicht: Dafür braucht es die Kursbewegung
/// innerhalb der Bar, die nur der Host kennt (Ausführungssimulation bzw. Broker).
///
/// Die Log-Senke <see cref="IStrategyLog"/> wird bewusst mit der Strategie-Bibliothek geteilt,
/// damit ein Host nur eine einzige Senke implementieren muss.
/// </remarks>
public sealed class ExecutionEngine
{
    private static readonly IReadOnlyList<ExecutionInstruction> NoInstructions = new ExecutionInstruction[0];

    private readonly ExecutionSymbol _symbol;
    private readonly RiskLimits _limits;
    private readonly RiskGate _riskGate;
    private readonly SessionGuard _sessionGuard;
    private readonly IStrategyLog _log;

    public ExecutionEngine(ExecutionSymbol symbol, RiskLimits limits, IStrategyLog? log = null)
    {
        _symbol = symbol ?? throw new ArgumentNullException(nameof(symbol));
        _limits = limits ?? throw new ArgumentNullException(nameof(limits));
        _log = log ?? NullStrategyLog.Instance;
        _riskGate = new RiskGate(symbol, limits);
        _sessionGuard = new SessionGuard(limits);
    }

    /// <summary>Die letzte Risikoentscheidung - für Diagnose und Auswertung der Ablehnungsgründe.</summary>
    public RiskDecision? LastDecision { get; private set; }

    /// <summary>
    /// Verarbeitet eine abgeschlossene Bar samt Strategiesignal.
    /// </summary>
    /// <param name="accountOpenRisk">
    /// Offene Positionen des gesamten Kontos mit aktuellem Preis. Wird nichts übergeben, nimmt
    /// die Schicht die Positionen dieses Symbols aus dem Snapshot und bewertet sie mit dem
    /// Bar-Close. Für ein Konto mit mehreren Symbolen muss der Host die vollständige Liste
    /// liefern, sonst gelten die Kontogrenzen nur je Symbol.
    /// </param>
    public IReadOnlyList<ExecutionInstruction> OnBar(
        MarketSnapshot snapshot,
        AccountState account,
        Signal? signal,
        IReadOnlyList<OpenRiskItem>? accountOpenRisk = null)
    {
        if (account == null)
        {
            throw new ArgumentNullException(nameof(account));
        }

        account.BeginTradingDay(snapshot.Session.TradingDay);

        var instructions = new List<ExecutionInstruction>();
        _sessionGuard.AppendExits(snapshot, snapshot.OpenPositions, instructions);

        if (signal != null && signal.Kind == SignalKind.CloseAll)
        {
            AppendStrategyExits(snapshot, instructions, signal.Reason);
        }

        if (signal != null && signal.Kind == SignalKind.Entry)
        {
            var openRisk = accountOpenRisk ?? BuildOpenRisk(snapshot);
            var decision = _riskGate.Evaluate(signal, snapshot, account, openRisk);
            LastDecision = decision;

            if (decision.Accepted)
            {
                account.RegisterTradeOpened();
                instructions.Add(ExecutionInstruction.Open(
                    snapshot.BarCloseTimeUtc,
                    _symbol.Name,
                    signal.Direction!.Value,
                    decision.Quantity,
                    signal.ReferencePrice,
                    signal.StopLoss!.Value,
                    signal.TakeProfit,
                    decision.RiskAmount,
                    decision.RiskPercent,
                    signal.Reason));

                _log.Info($"[Risk] {snapshot.BarCloseTimeUtc:yyyy-MM-dd HH:mm} {decision.Message}");
            }
            else
            {
                Log(decision, snapshot.BarCloseTimeUtc);
            }
        }

        return instructions.Count == 0 ? NoInstructions : instructions;
    }

    private void AppendStrategyExits(MarketSnapshot snapshot, List<ExecutionInstruction> instructions, string reason)
    {
        var positions = snapshot.OpenPositions;
        for (var i = 0; i < positions.Count; i++)
        {
            var position = positions[i];
            if (AlreadyClosing(instructions, position.Id))
            {
                continue;
            }

            instructions.Add(ExecutionInstruction.Close(
                snapshot.BarCloseTimeUtc,
                position.Symbol,
                position.Id,
                snapshot.Current.Close,
                ExitReason.StrategyExit,
                string.IsNullOrWhiteSpace(reason) ? "Ausstiegssignal der Strategie." : reason));
        }
    }

    private static bool AlreadyClosing(List<ExecutionInstruction> instructions, string positionId)
    {
        for (var i = 0; i < instructions.Count; i++)
        {
            if (instructions[i].Kind == ExecutionInstructionKind.ClosePosition
                && string.Equals(instructions[i].PositionId, positionId, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private IReadOnlyList<OpenRiskItem> BuildOpenRisk(MarketSnapshot snapshot)
    {
        var positions = snapshot.OpenPositions;
        if (positions.Count == 0)
        {
            return new OpenRiskItem[0];
        }

        // Positionen, die auf dieser Bar geschlossen werden sollen, zählen bewusst noch mit:
        // Geschlossen sind sie erst, wenn der Host sie tatsächlich geschlossen hat.
        var items = new OpenRiskItem[positions.Count];
        var price = snapshot.Current.Close;
        for (var i = 0; i < positions.Count; i++)
        {
            items[i] = new OpenRiskItem(positions[i], price, _symbol.ValuePerPricePointPerUnit);
        }

        return items;
    }

    private void Log(RiskDecision decision, DateTime timeUtc)
    {
        var text = $"[Risk] {timeUtc:yyyy-MM-dd HH:mm} {decision.Reason}: {decision.Message}";

        switch (decision.Reason)
        {
            case RiskRejectionReason.MissingStopLoss:
            case RiskRejectionReason.InvalidStopLoss:
                // Fehler in der Strategie, nicht im Markt.
                _log.Error(text);
                break;
            case RiskRejectionReason.TotalDrawdownLimit:
            case RiskRejectionReason.PositionTooSmallForRiskBudget:
                _log.Warning(text);
                break;
            default:
                _log.Info(text);
                break;
        }
    }
}
