using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using File = System.IO.File;
using Directory = System.IO.Directory;
using Path = System.IO.Path;
using cAlgo.API;
using Daytrading.Evaluation;
using Daytrading.Execution;
using Daytrading.Execution.Sessions;
using Daytrading.Strategies;
using Daytrading.Strategies.Model;
using Position = Daytrading.Strategies.Model.Position;
using SymbolInfo = Daytrading.Strategies.Model.SymbolInfo;

namespace Daytrading.CBot;

/// <summary>
/// Der cBot: ein dünner Adapter zwischen cTrader und dem Framework.
/// </summary>
/// <remarks>
/// Hier steht bewusst <b>keine</b> Handelslogik. Der Bot übersetzt Bars in einen
/// <see cref="MarketSnapshot"/>, fragt die gewählte Strategie, lässt die Ausführungsschicht
/// über Risiko und Tagesregeln entscheiden und führt deren Anweisungen aus. Damit trifft der
/// Livebetrieb dieselben Entscheidungen wie der Backtest - es ist derselbe Code.
///
/// Zeitpunkte: <c>OnBar</c> ruft cTrader auf, wenn eine neue Bar beginnt, die vorherige also
/// gerade geschlossen hat. Entschieden wird auf dieser geschlossenen Bar, ausgeführt sofort -
/// das ist genau die Open der Folgebar, mit der auch der Backtester rechnet.
///
/// Eine Strategie je Konto: Der Bot handelt nur Positionen mit seinem eigenen Label und sieht
/// keine anderen.
/// </remarks>
public abstract class DaytradingBotBase : Robot
{
    private readonly Dictionary<int, TradePlan> _plans = new Dictionary<int, TradePlan>();
    private readonly Dictionary<RiskRejectionReason, int> _rejections =
        new Dictionary<RiskRejectionReason, int>();

    private IStrategy _strategy = null!;
    private ExecutionEngine _engine = null!;
    private AccountState _account = null!;
    private TradingSessionCalendar _calendar = null!;
    private StrategyHealthMonitor? _monitor;
    private BarSeries _history = null!;
    private Timeframe _timeframe;
    private SymbolInfo _symbolInfo = null!;
    private string _label = string.Empty;
    private string? _tradeLogPath;
    private DateTime _lastBarTime = DateTime.MinValue;

    /// <summary>
    /// Name der Strategie im <see cref="StrategyCatalog"/>.
    /// </summary>
    /// <remarks>
    /// Die abgeleiteten Bots legen ihn fest, statt ihn als Parameter anzubieten. Ein Konto
    /// haelt damit genau eine Strategie, und der Name im cTrader-Verzeichnis sagt, welche -
    /// das ist die Voraussetzung dafuer, Ergebnisse ueberhaupt einer Strategie zuordnen
    /// zu koennen.
    /// </remarks>
    protected abstract string StrategyName { get; }

    [Parameter("Parameter (Name=Wert;Name=Wert)", DefaultValue = "", Group = "Strategie")]
    public string StrategyParameterText { get; set; } = string.Empty;

    [Parameter("Anlageklasse", DefaultValue = AssetClass.Equity, Group = "Strategie")]
    public AssetClass Asset { get; set; } = AssetClass.Equity;

    [Parameter("Zeitzone der Börse", DefaultValue = "UTC", Group = "Session")]
    public string TimeZoneId { get; set; } = "UTC";

    [Parameter("Sessionbeginn (Ortszeit)", DefaultValue = "09:30", Group = "Session")]
    public string SessionStart { get; set; } = "09:30";

    [Parameter("Sessionende (Ortszeit)", DefaultValue = "16:00", Group = "Session")]
    public string SessionEnd { get; set; } = "16:00";

    [Parameter("Handelstage", DefaultValue = "Monday,Tuesday,Wednesday,Thursday,Friday", Group = "Session")]
    public string TradingDays { get; set; } = "Monday,Tuesday,Wednesday,Thursday,Friday";

    [Parameter("Feiertagskalender", DefaultValue = "none", Group = "Session")]
    public string HolidayCalendar { get; set; } = "none";

    [Parameter("Risiko je Trade %", DefaultValue = 1.0, MinValue = 0.01, MaxValue = 100, Group = "Risiko")]
    public double RiskPerTradePercent { get; set; } = 1.0;

    [Parameter("Max. offenes Risiko %", DefaultValue = 4.0, MinValue = 0.01, MaxValue = 100, Group = "Risiko")]
    public double MaxOpenRiskPercent { get; set; } = 4.0;

    [Parameter("Max. Tagesverlust %", DefaultValue = 4.0, MinValue = 0.01, MaxValue = 100, Group = "Risiko")]
    public double MaxDailyLossPercent { get; set; } = 4.0;

    [Parameter("Max. Tages-Drawdown %", DefaultValue = 4.0, MinValue = 0.01, MaxValue = 100, Group = "Risiko")]
    public double MaxDailyDrawdownPercent { get; set; } = 4.0;

    [Parameter("Max. Gesamt-Drawdown %", DefaultValue = 10.0, MinValue = 0.01, MaxValue = 100, Group = "Risiko")]
    public double MaxTotalDrawdownPercent { get; set; } = 10.0;

    [Parameter("Max. Kostenanteil am Risiko %", DefaultValue = 100.0, MinValue = 1, MaxValue = 100, Group = "Risiko")]
    public double MaxCostShareOfRiskPercent { get; set; } = 100.0;

    [Parameter("Drawdown gemessen ab", DefaultValue = DrawdownBasis.TrailingPeak, Group = "Risiko")]
    public DrawdownBasis TotalDrawdownBasis { get; set; } = DrawdownBasis.TrailingPeak;

    [Parameter("Max. gleichzeitige Positionen", DefaultValue = 5, MinValue = 1, Group = "Risiko")]
    public int MaxConcurrentPositions { get; set; } = 5;

    [Parameter("Max. Trades pro Tag", DefaultValue = 6, MinValue = 1, Group = "Risiko")]
    public int MaxTradesPerDay { get; set; } = 6;

    [Parameter("Flat X Minuten vor Sessionende", DefaultValue = 15, MinValue = 1, Group = "Risiko")]
    public int ForceFlatMinutes { get; set; } = 15;

    [Parameter("Max. Haltedauer in Minuten (0 = aus)", DefaultValue = 0, MinValue = 0, Group = "Risiko")]
    public int MaxHoldingMinutes { get; set; }

    [Parameter("Erwartungswert R (Mittel)", DefaultValue = 0.0, Group = "Erwartungsprofil")]
    public double ProfileExpectancyMean { get; set; }

    [Parameter("Erwartungswert R (Streuung)", DefaultValue = 0.0, Group = "Erwartungsprofil")]
    public double ProfileExpectancyDeviation { get; set; }

    [Parameter("Schlechtester Drawdown in R", DefaultValue = 0.0, Group = "Erwartungsprofil")]
    public double ProfileWorstDrawdownR { get; set; }

    [Parameter("Längste Verlustserie", DefaultValue = 0, Group = "Erwartungsprofil")]
    public int ProfileLongestLosingStreak { get; set; }

    [Parameter("Trades pro Woche", DefaultValue = 0.0, Group = "Erwartungsprofil")]
    public double ProfileTradesPerWeek { get; set; }

    [Parameter("Angenommener Spread", DefaultValue = 0.0, Group = "Erwartungsprofil")]
    public double ProfileAssumedSpread { get; set; }

    [Parameter("Trade-Log-Verzeichnis", DefaultValue = "", Group = "Protokoll")]
    public string TradeLogDirectory { get; set; } = string.Empty;

    [Parameter("Ausführliches Log", DefaultValue = false, Group = "Protokoll")]
    public bool Verbose { get; set; }

    protected override void OnStart()
    {
        _label = $"{StrategyName}-{InstanceId}";
        _symbolInfo = new SymbolInfo(SymbolName, Asset, (decimal)Symbol.TickSize);
        _history = new BarSeries();

        var limits = new RiskLimits
        {
            RiskPerTradePercent = (decimal)RiskPerTradePercent,
            MaxOpenRiskPercent = (decimal)MaxOpenRiskPercent,
            MaxDailyLossPercent = (decimal)MaxDailyLossPercent,
            MaxDailyDrawdownPercent = (decimal)MaxDailyDrawdownPercent,
            MaxTotalDrawdownPercent = (decimal)MaxTotalDrawdownPercent,
            TotalDrawdownBasis = TotalDrawdownBasis,
            MaxCostShareOfRiskPercent = (decimal)MaxCostShareOfRiskPercent,
            MaxConcurrentPositions = MaxConcurrentPositions,
            MaxTradesPerDay = MaxTradesPerDay,
            ForceFlatMinutesBeforeSessionEnd = ForceFlatMinutes,
            MaxHoldingMinutes = MaxHoldingMinutes > 0 ? MaxHoldingMinutes : (int?)null,
        };

        limits.Validate();

        _calendar = CTraderBridge.ToSessionCalendar(
            TimeZoneId, SessionStart, SessionEnd, TradingDays, HolidayCalendar, SymbolName);

        _timeframe = DetectTimeframe();
        _strategy = StrategyCatalog.Create(StrategyName);
        PreloadHistory();
        ReportCostFilter();

        var log = new CTraderLog(Print, Verbose);
        var parameters = StrategyCatalog.ParseParameters(StrategyParameterText);
        _strategy.Initialize(new StrategyContext(_symbolInfo, _timeframe, parameters, log));

        foreach (var unused in parameters.UnusedKeys)
        {
            Print($"WARNUNG: Parameter '{unused}' wird von {StrategyName} nicht gelesen - Tippfehler?");
        }

        _engine = new ExecutionEngine(CTraderBridge.ToExecutionSymbol(Symbol, Asset), limits, log);
        _account = new AccountState((decimal)Account.Balance);
        _monitor = CreateMonitor();

        if (_timeframe.Duration.TotalMinutes > ForceFlatMinutes)
        {
            // Der Bot entscheidet nur zum Barschluss. Ist die Bar länger als das Zwangsfenster,
            // kann die Schließung erst nach Sessionende ausgelöst werden.
            Print(
                $"WARNUNG: Bargröße {_timeframe} ist größer als das Zwangsschließungsfenster von " +
                $"{ForceFlatMinutes} Minuten. Fenster vergrößern oder feinere Bars wählen.");
        }

        PrepareTradeLog();
        Positions.Closed += OnPositionClosed;

        Print(
            $"{StrategyName} gestartet auf {SymbolName} ({_timeframe}), Label {_label}, " +
            $"Risiko {RiskPerTradePercent} % je Trade, Session {SessionStart}-{SessionEnd} {TimeZoneId}.");
    }

    protected override void OnBar()
    {
        if (Bars.Count < 2)
        {
            return;
        }

        var closed = Bars.Last(1);
        var openTime = DateTime.SpecifyKind(closed.OpenTime, DateTimeKind.Utc);
        if (openTime <= _lastBarTime)
        {
            return;
        }

        _lastBarTime = openTime;
        _history.Append(CTraderBridge.ToCandle(closed));

        var barClose = openTime + _timeframe.Duration;
        if (!_calendar.TryGetSessionAt(barClose, out var session))
        {
            return;
        }

        _account.SyncFromBroker((decimal)Account.Balance, (decimal)Account.Equity);

        var positions = OwnPositions();
        var snapshot = new MarketSnapshot(_symbolInfo, _timeframe, _history, session, positions);

        Signal? signal;
        try
        {
            signal = _strategy.OnBar(snapshot);
        }
        catch (Exception error)
        {
            // Ein Fehler in der Strategie darf offene Positionen nicht führungslos lassen.
            Print($"FEHLER in {StrategyName}: {error.Message}. Es wird nur noch verwaltet, nicht neu eröffnet.");
            signal = null;
        }

        foreach (var instruction in _engine.OnBar(snapshot, _account, signal))
        {
            Apply(instruction);
        }

        // Ein abgelehntes Einstiegssignal ist die haeufigste Ursache dafuer, dass ein Bot
        // scheinbar nichts tut. Es darf nicht stumm verschwinden.
        if (signal is { Kind: SignalKind.Entry } && _engine.LastDecision is { Accepted: false } decision)
        {
            ReportRejection(decision);
        }
    }

    protected override void OnStop()
    {
        Positions.Closed -= OnPositionClosed;

        if (_monitor != null)
        {
            Print($"Zustand bei Stopp: {_monitor.State}, Risikofaktor {_monitor.RiskMultiplier}.");
        }

        foreach (var pair in _rejections)
        {
            Print($"Abgelehnte Einstiege - {pair.Key}: {pair.Value}");
        }
    }

    /// <summary>
    /// Füllt die Kursreihe mit der Historie, die im Chart bereits geladen ist.
    /// </summary>
    /// <remarks>
    /// Ohne das beginnt der Bot bei null Bars und muss seine Aufwärmphase in Echtzeit
    /// nachholen: Eine Strategie mit 120 Bars Vorlauf schweigt auf einem 5-Minuten-Chart zehn
    /// Stunden lang. Das sah von außen aus wie ein Bot, der nicht handelt, und war doch nur
    /// einer, der noch rechnet.
    ///
    /// Schlimmer als das Warten war die Abweichung: Der Backtester bekommt die volle Reihe,
    /// der cBot fing bei null an. Damit lieferten dieselben Regeln auf denselben Kursen
    /// verschiedene Ergebnisse - und der Backtest sagte nichts mehr über den Livebetrieb.
    ///
    /// Die laufende, noch nicht abgeschlossene Bar bleibt außen vor; übernommen wird nur, was
    /// fertig ist.
    /// </remarks>
    private void PreloadHistory()
    {
        var wanted = Math.Max(_strategy.WarmupBars * 2, 200);
        var available = Bars.Count - 1;              // ohne die laufende Bar
        var take = Math.Min(wanted, Math.Max(0, available));

        for (var i = take; i >= 1; i--)
        {
            var bar = Bars.Last(i);
            _history.Append(CTraderBridge.ToCandle(bar));
            _lastBarTime = DateTime.SpecifyKind(bar.OpenTime, DateTimeKind.Utc);
        }

        if (take < _strategy.WarmupBars)
        {
            Print(
                $"WARNUNG: Nur {take} Bars Historie im Chart, {StrategyName} braucht {_strategy.WarmupBars}. " +
                "Bis dahin kommen keine Signale. Im Chart weiter zurueckscrollen laedt mehr Historie.");
        }
        else
        {
            Print($"{take} Bars Historie uebernommen, Vorlauf von {StrategyName} ist gedeckt.");
        }
    }

    /// <summary>
    /// Rechnet die Kostengrenze in den Stopabstand um, den sie mindestens verlangt.
    /// </summary>
    /// <remarks>
    /// Als Prozentzahl ist diese Einstellung nicht zu beurteilen: Ob "10 %" jeden Trade
    /// durchlässt oder jeden ablehnt, hängt am Spread des Instruments und an der Größe der
    /// Stops, die die Strategie setzt. Auf Gold im 5-Minuten-Takt liegt ein Stop von 1.5 ATR
    /// bei rund 1.8 Punkten - bei 0.30 Spread sind das 16 %, und ein 10-%-Filter lehnt
    /// wortlos alles ab.
    ///
    /// Deshalb steht die Zahl beim Start als Preisabstand im Log, wo sie mit dem Chart
    /// vergleichbar ist.
    /// </remarks>
    private void ReportCostFilter()
    {
        if (MaxCostShareOfRiskPercent >= 100)
        {
            return;
        }

        var valuePerPoint = Symbol.PipSize > 0 ? Symbol.PipValue / Symbol.PipSize : 1d;
        var costPerUnit = (Symbol.Spread * Math.Max(valuePerPoint, 0.0000001d)) + (2d * Math.Max(Symbol.Commission, 0d));
        var minStop = costPerUnit / (MaxCostShareOfRiskPercent / 100d) / Math.Max(valuePerPoint, 0.0000001d);

        Print(
            $"Kostenfilter {MaxCostShareOfRiskPercent} %: Einstiege brauchen mindestens {minStop:0.#####} " +
            $"Kursabstand zum Stop (Spread gerade {Symbol.Spread:0.#####}). Engere Stops werden abgelehnt.");
    }

    /// <summary>
    /// Meldet, warum ein Signal nicht zu einem Trade wurde.
    /// </summary>
    /// <remarks>
    /// Ein Bot, der ohne ein Wort nicht handelt, ist nicht zu beurteilen: Von außen sieht eine
    /// abgelehnte Strategie genauso aus wie eine, die kein Signal hat, und wie ein Fehler. Die
    /// erste Ablehnung je Grund wird deshalb immer gemeldet, danach jede fünfzigste - das
    /// bleibt lesbar und geht trotzdem nicht unter.
    /// </remarks>
    private void ReportRejection(RiskDecision decision)
    {
        _rejections.TryGetValue(decision.Reason, out var seen);
        _rejections[decision.Reason] = ++seen;

        if (seen == 1 || seen % 50 == 0)
        {
            Print($"Kein Einstieg ({decision.Reason}, {seen}. Mal): {decision.Message}");
        }
    }

    private void Apply(ExecutionInstruction instruction)
    {
        if (instruction.Kind == ExecutionInstructionKind.ClosePosition)
        {
            var position = Positions.FindById(int.Parse(instruction.PositionId!, CultureInfo.InvariantCulture));
            if (position == null)
            {
                return;
            }

            var result = ClosePosition(position);
            Print(result.IsSuccessful
                ? $"Geschlossen #{position.Id}: {instruction.ExitReason} - {instruction.Reason}"
                : $"FEHLER beim Schließen #{position.Id}: {result.Error}");
            return;
        }

        var volume = Symbol.NormalizeVolumeInUnits((double)instruction.Quantity, RoundingMode.Down);
        if (volume < Symbol.VolumeInUnitsMin)
        {
            Print(
                $"Order verworfen: {instruction.Quantity} Einheiten liegen nach Normalisierung unter der " +
                $"Mindestgröße {Symbol.VolumeInUnitsMin}.");
            return;
        }

        // Ohne Stop und Ziel eröffnen und beide unmittelbar danach auf den absoluten Preis setzen:
        // ExecuteMarketOrder kennt nur Pips, das Framework rechnet in Preisen.
        var trade = ExecuteMarketOrder(
            CTraderBridge.ToTradeType(instruction.Direction!.Value),
            SymbolName,
            volume,
            _label,
            (double?)null,
            (double?)null);

        if (!trade.IsSuccessful || trade.Position == null)
        {
            Print($"FEHLER beim Öffnen: {trade.Error}");
            return;
        }

        var opened = trade.Position;
        var stopResult = opened.ModifyStopLossPrice((double)instruction.StopLoss!.Value);

        if (!stopResult.IsSuccessful)
        {
            // Eine Position ohne Stop widerspricht der Grundregel des Frameworks - sofort schließen.
            Print($"FEHLER: Stop konnte nicht gesetzt werden ({stopResult.Error}). Position wird geschlossen.");
            ClosePosition(opened);
            return;
        }

        if (instruction.TakeProfit.HasValue)
        {
            opened.ModifyTakeProfitPrice((double)instruction.TakeProfit.Value);
        }

        _plans[opened.Id] = new TradePlan(instruction.RiskAmount, instruction.RiskPercent, (decimal)Symbol.Spread);

        Print(
            $"Eröffnet #{opened.Id} {instruction.Direction} {volume} @{opened.EntryPrice} " +
            $"SL={instruction.StopLoss} TP={instruction.TakeProfit} Risiko={instruction.RiskAmount:0.00} " +
            $"({instruction.RiskPercent:0.00} %) - {instruction.Reason}");
    }

    private void OnPositionClosed(PositionClosedEventArgs args)
    {
        var position = args.Position;
        if (!string.Equals(position.Label, _label, StringComparison.Ordinal))
        {
            return;
        }

        _plans.TryGetValue(position.Id, out var plan);
        _plans.Remove(position.Id);

        var risk = plan?.RiskAmount ?? 0m;
        var net = (decimal)position.NetProfit;
        var rMultiple = risk > 0m ? net / risk : 0m;

        WriteTradeLog(position, plan, net, args.Reason);

        if (_monitor == null)
        {
            return;
        }

        var before = _monitor.State;
        var assessment = _monitor.Observe(new TradeObservation(
            Server.TimeInUtc, rMultiple, plan?.SpreadAtEntry ?? 0m, 0m));

        foreach (var finding in assessment.Findings)
        {
            Print($"[Bewertung] {finding}");
        }

        if (assessment.State != before)
        {
            var change = _monitor.History[_monitor.History.Count - 1];
            Print($"[Bewertung] Zustand {change.From} -> {change.To}: {change.Trigger} | {change.DataBasis}");
        }

        // Der Faktor wirkt ab dem nächsten Signal: 1.0 normal, 0.5 bei Degraded, 0 bei Suspended.
        _engine.RiskMultiplier = _monitor.RiskMultiplier;
    }

    private IReadOnlyList<Position> OwnPositions()
    {
        var result = new List<Position>();
        foreach (var position in Positions.FindAll(_label, SymbolName))
        {
            result.Add(CTraderBridge.ToPosition(position, _label));
        }

        return result;
    }

    private StrategyHealthMonitor? CreateMonitor()
    {
        if (ProfileExpectancyMean == 0d && ProfileWorstDrawdownR == 0d && ProfileLongestLosingStreak == 0)
        {
            Print(
                "Kein Erwartungsprofil hinterlegt - die laufende Selbstkontrolle ist aus. " +
                "Werte stehen in results/profiles/*.json aus der Walk-Forward-Analyse.");
            return null;
        }

        var profile = new ExpectationProfile(
            StrategyName,
            SymbolName,
            DateTime.UtcNow,
            windowCount: 0,
            totalTrades: 0,
            new Statistic((decimal)ProfileExpectancyMean, (decimal)ProfileExpectancyDeviation, 0m, 0m, 0),
            new Statistic(0m, 0m, 0m, 0m, 0),
            new Statistic((decimal)ProfileTradesPerWeek, 0m, 0m, 0m, 0),
            new Statistic(0m, 0m, 0m, 0m, 0),
            (decimal)ProfileWorstDrawdownR,
            ProfileLongestLosingStreak,
            (decimal)ProfileAssumedSpread,
            strategyVersion: _strategy.Descriptor.Version);

        return new StrategyHealthMonitor(profile);
    }

    private void PrepareTradeLog()
    {
        if (string.IsNullOrWhiteSpace(TradeLogDirectory))
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(TradeLogDirectory);
            _tradeLogPath = Path.Combine(
                TradeLogDirectory,
                $"{StrategyName}_{SymbolName}_{InstanceId}.csv".Replace('/', '-'));

            if (!File.Exists(_tradeLogPath))
            {
                File.WriteAllText(_tradeLogPath, TradeRecordCsv.Header + Environment.NewLine);
            }

            Print($"Trade-Log: {_tradeLogPath}");
        }
        catch (Exception error)
        {
            // Kein Grund, den Handel zu stoppen - aber es muss auffallen.
            Print($"WARNUNG: Trade-Log konnte nicht angelegt werden: {error.Message}");
            _tradeLogPath = null;
        }
    }

    private void WriteTradeLog(cAlgo.API.Position position, TradePlan? plan, decimal net, PositionCloseReason reason)
    {
        if (_tradeLogPath == null)
        {
            return;
        }

        try
        {
            var neutral = CTraderBridge.ToPosition(position, _label);
            var record = new TradeRecord(
                _strategy.Descriptor.Key,
                neutral,
                Server.TimeInUtc,
                (decimal)position.CurrentPrice,
                ToExitReason(reason),
                net + (decimal)position.Commissions,
                Math.Abs((decimal)position.Commissions),
                plan?.RiskAmount ?? 0m,
                plan?.RiskPercent ?? 0m);

            File.AppendAllText(_tradeLogPath, TradeRecordCsv.Format(record) + Environment.NewLine);
        }
        catch (Exception error)
        {
            Print($"WARNUNG: Trade konnte nicht protokolliert werden: {error.Message}");
        }
    }

    private static ExitReason ToExitReason(PositionCloseReason reason) => reason switch
    {
        PositionCloseReason.StopLoss => ExitReason.StopLoss,
        PositionCloseReason.TakeProfit => ExitReason.TakeProfit,
        _ => ExitReason.StrategyExit,
    };

    private Timeframe DetectTimeframe()
    {
        // Aus dem Abstand zweier Bars statt aus TimeFrame: robust gegen Benennungsunterschiede.
        if (Bars.Count >= 2)
        {
            var span = Bars.Last(0).OpenTime - Bars.Last(1).OpenTime;
            if (span > TimeSpan.Zero)
            {
                return new Timeframe(span);
            }
        }

        Print("WARNUNG: Bargröße konnte nicht aus den Daten bestimmt werden, es wird M15 angenommen.");
        return Timeframe.M15;
    }

    private sealed class TradePlan
    {
        public TradePlan(decimal riskAmount, decimal riskPercent, decimal spreadAtEntry)
        {
            RiskAmount = riskAmount;
            RiskPercent = riskPercent;
            SpreadAtEntry = spreadAtEntry;
        }

        public decimal RiskAmount { get; }

        public decimal RiskPercent { get; }

        public decimal SpreadAtEntry { get; }
    }
}
