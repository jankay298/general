using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Daytrading.Evaluation;

/// <summary>Zustand einer laufenden Kombination aus Strategie und Symbol.</summary>
public enum HealthState
{
    /// <summary>Im erwarteten Bereich. Volles Risiko.</summary>
    Healthy = 0,

    /// <summary>Auffällig, aber innerhalb der historischen Streuung. Nur beobachten, nichts ändern.</summary>
    Watch = 1,

    /// <summary>Messbar außerhalb der Erwartung. Risiko je Trade wird halbiert.</summary>
    Degraded = 2,

    /// <summary>Kein neuer Trade mehr. Bestehende Positionen laufen regulär zu Ende.</summary>
    Suspended = 3,
}

/// <summary>Ein beobachteter Trade aus Demo- oder Livebetrieb.</summary>
public sealed class TradeObservation
{
    public TradeObservation(DateTime closedAtUtc, decimal rMultiple, decimal realizedSpread = 0m, decimal slippage = 0m)
    {
        ClosedAtUtc = closedAtUtc;
        RMultiple = rMultiple;
        RealizedSpread = realizedSpread;
        Slippage = slippage;
    }

    public DateTime ClosedAtUtc { get; }

    public decimal RMultiple { get; }

    /// <summary>Tatsächlich bezahlter Spread. Für die Unterscheidung Strategie- vs. Ausführungsproblem.</summary>
    public decimal RealizedSpread { get; }

    public decimal Slippage { get; }
}

/// <summary>Warum sich ein Zustand geändert hat - mit Zeitstempel und Datengrundlage.</summary>
public sealed class HealthStateChange
{
    public HealthStateChange(DateTime timestampUtc, HealthState from, HealthState to, string trigger, string dataBasis)
    {
        TimestampUtc = timestampUtc;
        From = from;
        To = to;
        Trigger = trigger;
        DataBasis = dataBasis;
    }

    public DateTime TimestampUtc { get; }

    public HealthState From { get; }

    public HealthState To { get; }

    public string Trigger { get; }

    /// <summary>Worauf die Entscheidung beruhte - Anzahl Trades, gemessene Werte, Schwellen.</summary>
    public string DataBasis { get; }

    public override string ToString() =>
        string.Format(
            CultureInfo.InvariantCulture,
            "{0:yyyy-MM-dd HH:mm} {1} -> {2}: {3} [{4}]", TimestampUtc, From, To, Trigger, DataBasis);
}

/// <summary>Schwellen der Zustandsüberwachung. Alle konfigurierbar, keine im Code vergraben.</summary>
public sealed class HealthThresholds
{
    /// <summary>Trades, die für den rollierenden Erwartungswert betrachtet werden.</summary>
    public int RollingTradeWindow { get; set; } = 30;

    /// <summary>Mindestanzahl Trades, bevor überhaupt ein Zustand verschlechtert wird.</summary>
    public int MinimumTradesForDowngrade { get; set; } = 20;

    /// <summary>Mindestanzahl Trades, bevor abgeschaltet wird.</summary>
    public int MinimumTradesForSuspension { get; set; } = 25;

    /// <summary>Wie viele Standardabweichungen unter dem Mittel als "außerhalb der Erwartung" gelten.</summary>
    public decimal ConfidenceBandSigma { get; set; } = 2m;

    /// <summary>Ab welcher Abweichung die Handelsfrequenz auffällig ist (Faktor gegenüber der Erwartung).</summary>
    public decimal FrequencyDeviationFactor { get; set; } = 2m;

    /// <summary>Ab welchem Vielfachen der Spread-Annahme ein Ausführungsproblem gemeldet wird.</summary>
    public decimal SpreadDeviationFactor { get; set; } = 1.5m;

    /// <summary>Abkühlphase, bevor eine abgeschaltete Kombination neu bewertet werden darf.</summary>
    public int CooldownDays { get; set; } = 30;

    public static HealthThresholds Default => new HealthThresholds();
}

/// <summary>Ergebnis einer Bewertung.</summary>
public sealed class HealthAssessment
{
    public HealthAssessment(HealthState state, decimal riskMultiplier, IReadOnlyList<string> findings, bool executionProblem)
    {
        State = state;
        RiskMultiplier = riskMultiplier;
        Findings = findings;
        ExecutionProblem = executionProblem;
    }

    public HealthState State { get; }

    /// <summary>Faktor auf das Risiko je Trade: 1.0 normal, 0.5 bei Degraded, 0 bei Suspended.</summary>
    public decimal RiskMultiplier { get; }

    public IReadOnlyList<string> Findings { get; }

    /// <summary>
    /// True, wenn Spread oder Slippage deutlich von der Backtest-Annahme abweichen. Das ist ein
    /// Ausführungsproblem und <b>kein</b> Strategieproblem - die Unterscheidung entscheidet
    /// darüber, ob man den Broker wechselt oder die Strategie abschaltet.
    /// </summary>
    public bool ExecutionProblem { get; }
}

/// <summary>
/// Hält den laufenden Betrieb gegen das eingefrorene Erwartungsprofil.
/// </summary>
/// <remarks>
/// Ausdrücklich <b>regelbasiert und nicht selbstoptimierend</b>: Der Monitor ändert keine
/// Parameter. Er senkt das Risiko, stoppt neue Trades oder meldet ein Ausführungsproblem -
/// mehr nicht. Eine laufende Anpassung von Parametern an Live-Ergebnisse würde sich an
/// Rauschen anpassen.
///
/// Vor jeder Verschlechterung steht eine Mindeststichprobe. Nach fünf Trades wird nichts
/// abgeschaltet, auch wenn sie alle schlecht waren.
///
/// Jede Zustandsänderung landet mit Zeitstempel, Auslöser und Datengrundlage in
/// <see cref="History"/>. Stille Anpassungen gibt es nicht.
/// </remarks>
public sealed class StrategyHealthMonitor
{
    private readonly ExpectationProfile _profile;
    private readonly HealthThresholds _thresholds;
    private readonly List<TradeObservation> _trades = new List<TradeObservation>();
    private readonly List<HealthStateChange> _history = new List<HealthStateChange>();

    private decimal _cumulativeR;
    private decimal _peakR;
    private int _losingStreak;
    private DateTime? _suspendedAtUtc;
    private DateTime? _firstTradeUtc;

    public StrategyHealthMonitor(ExpectationProfile profile, HealthThresholds? thresholds = null)
    {
        _profile = profile ?? throw new ArgumentNullException(nameof(profile));
        _thresholds = thresholds ?? HealthThresholds.Default;
    }

    public HealthState State { get; private set; } = HealthState.Healthy;

    public decimal RiskMultiplier => State switch
    {
        HealthState.Degraded => 0.5m,
        HealthState.Suspended => 0m,
        _ => 1m,
    };

    public int TradeCount => _trades.Count;

    public decimal CurrentDrawdownR => _peakR - _cumulativeR;

    public IReadOnlyList<HealthStateChange> History => _history;

    /// <summary>Verarbeitet einen abgeschlossenen Trade und bewertet den Zustand neu.</summary>
    public HealthAssessment Observe(TradeObservation trade)
    {
        if (trade == null)
        {
            throw new ArgumentNullException(nameof(trade));
        }

        _trades.Add(trade);
        _firstTradeUtc ??= trade.ClosedAtUtc;

        _cumulativeR += trade.RMultiple;
        if (_cumulativeR > _peakR)
        {
            _peakR = _cumulativeR;
        }

        _losingStreak = trade.RMultiple < 0m ? _losingStreak + 1 : 0;

        return Evaluate(trade.ClosedAtUtc);
    }

    /// <summary>Bewertet den Zustand ohne neuen Trade - etwa beim Prüfen der Abkühlphase.</summary>
    public HealthAssessment Evaluate(DateTime nowUtc)
    {
        var findings = new List<string>();
        var executionProblem = CheckExecution(findings);

        // Der Zustand wird bei jeder Bewertung neu bestimmt und nicht fortgeschrieben: Normalisiert
        // sich das Bild, gilt wieder Healthy. Nur Suspended ist endgültig und wird ausschließlich
        // über eine bestandene Neuvalidierung verlassen.
        var target = HealthState.Healthy;
        var trigger = string.Empty;
        var basis = string.Empty;

        if (State == HealthState.Suspended)
        {
            // Aus Suspended führt kein automatischer Weg zurück: dafür braucht es einen
            // frischen Out-of-Sample-Test über SubmitRevalidation.
            if (_suspendedAtUtc.HasValue && (nowUtc - _suspendedAtUtc.Value).TotalDays >= _thresholds.CooldownDays)
            {
                findings.Add(string.Format(
                    CultureInfo.InvariantCulture,
                    "Abkühlphase von {0} Tagen ist vorbei. Reaktivierung erst nach bestandenem Out-of-Sample-Test.",
                    _thresholds.CooldownDays));
            }

            return new HealthAssessment(State, RiskMultiplier, findings, executionProblem);
        }

        var enoughForSuspension = _trades.Count >= _thresholds.MinimumTradesForSuspension;
        var enoughForDowngrade = _trades.Count >= _thresholds.MinimumTradesForDowngrade;

        // 1. Drawdown über dem im Backtest beobachteten Maximum.
        if (CurrentDrawdownR > _profile.WorstDrawdownR && _profile.WorstDrawdownR > 0m)
        {
            var message = string.Format(
                CultureInfo.InvariantCulture,
                "Drawdown {0:0.##} R über dem historischen Maximum von {1:0.##} R.",
                CurrentDrawdownR, _profile.WorstDrawdownR);

            if (enoughForSuspension)
            {
                target = HealthState.Suspended;
                trigger = "Drawdown über historischem Maximum";
                basis = message;
            }
            else
            {
                findings.Add(message + $" Noch keine Entscheidung: {_trades.Count} von {_thresholds.MinimumTradesForSuspension} Trades.");
                target = Worse(target, HealthState.Watch);
            }
        }

        // 2. Verlustserie länger als je beobachtet.
        if (target != HealthState.Suspended && _losingStreak > _profile.LongestLosingStreak && _profile.LongestLosingStreak > 0)
        {
            var message = string.Format(
                CultureInfo.InvariantCulture,
                "Verlustserie von {0} Trades über der längsten historischen von {1}.",
                _losingStreak, _profile.LongestLosingStreak);

            if (enoughForSuspension)
            {
                target = HealthState.Suspended;
                trigger = "Verlustserie über historischem Maximum";
                basis = message;
            }
            else
            {
                findings.Add(message + $" Noch keine Entscheidung: {_trades.Count} von {_thresholds.MinimumTradesForSuspension} Trades.");
                target = Worse(target, HealthState.Watch);
            }
        }

        // 3. Rollierender Erwartungswert unter dem unteren Konfidenzband.
        if (target != HealthState.Suspended && _trades.Count > 0)
        {
            var window = Math.Min(_thresholds.RollingTradeWindow, _trades.Count);
            var rolling = _trades.Skip(_trades.Count - window).Average(observation => observation.RMultiple);
            var lower = _profile.ExpectancyR.LowerBand(_thresholds.ConfidenceBandSigma);

            if (rolling < lower)
            {
                var message = string.Format(
                    CultureInfo.InvariantCulture,
                    "Rollierender Erwartungswert {0:0.###} R über {1} Trades unter dem unteren Band {2:0.###} R.",
                    rolling, window, lower);

                if (enoughForDowngrade)
                {
                    target = Worse(target, HealthState.Degraded);
                    trigger = "Erwartungswert unter dem Konfidenzband";
                    basis = message;
                }
                else
                {
                    findings.Add(message + $" Noch keine Entscheidung: {_trades.Count} von {_thresholds.MinimumTradesForDowngrade} Trades.");
                    target = Worse(target, HealthState.Watch);
                }
            }
        }

        // 4. Handelsfrequenz: Hinweis auf Regimewechsel oder Fehler, kein Qualitätsurteil.
        // Erst ab ausreichender Stichprobe und mindestens einer vollen Woche - über zwei Trades
        // an einem Tag lässt sich keine Wochenfrequenz schätzen.
        if (_firstTradeUtc.HasValue
            && _profile.TradesPerWeek.Mean > 0m
            && _trades.Count >= _thresholds.MinimumTradesForDowngrade
            && (nowUtc - _firstTradeUtc.Value).TotalDays >= 7)
        {
            var weeks = Math.Max((decimal)(nowUtc - _firstTradeUtc.Value).TotalDays / 7m, 0.1m);
            var actual = _trades.Count / weeks;
            var expected = _profile.TradesPerWeek.Mean;

            if (actual > expected * _thresholds.FrequencyDeviationFactor
                || actual < expected / _thresholds.FrequencyDeviationFactor)
            {
                findings.Add(string.Format(
                    CultureInfo.InvariantCulture,
                    "Handelsfrequenz {0:0.##} statt {1:0.##} Trades pro Woche. Hinweis auf Regimewechsel oder einen Fehler, " +
                    "nicht auf eine schlechte Strategie.",
                    actual, expected));
                target = Worse(target, HealthState.Watch);
            }
        }

        if (target != State)
        {
            var from = State;
            State = target;

            if (target == HealthState.Suspended)
            {
                _suspendedAtUtc = nowUtc;
            }

            _history.Add(new HealthStateChange(
                nowUtc,
                from,
                target,
                string.IsNullOrEmpty(trigger) ? string.Join(" | ", findings) : trigger,
                string.IsNullOrEmpty(basis)
                    ? $"{_trades.Count} Trades beobachtet, Profil aus {_profile.WindowCount} Fenstern"
                    : basis + $" ({_trades.Count} Trades beobachtet)"));
        }

        return new HealthAssessment(State, RiskMultiplier, findings, executionProblem);
    }

    /// <summary>
    /// Ergebnis einer Neuvalidierung einreichen. Nur ein bestandener frischer Out-of-Sample-Test
    /// holt eine abgeschaltete Kombination zurück.
    /// </summary>
    public void SubmitRevalidation(bool passed, DateTime nowUtc, string dataBasis)
    {
        if (State != HealthState.Suspended)
        {
            return;
        }

        if (!_suspendedAtUtc.HasValue || (nowUtc - _suspendedAtUtc.Value).TotalDays < _thresholds.CooldownDays)
        {
            _history.Add(new HealthStateChange(
                nowUtc, State, State, "Neuvalidierung abgelehnt: Abkühlphase läuft noch", dataBasis ?? string.Empty));
            return;
        }

        if (!passed)
        {
            _history.Add(new HealthStateChange(
                nowUtc, State, State, "Neuvalidierung nicht bestanden", dataBasis ?? string.Empty));
            return;
        }

        _history.Add(new HealthStateChange(
            nowUtc, State, HealthState.Watch, "Neuvalidierung bestanden", dataBasis ?? string.Empty));

        State = HealthState.Watch;
        _suspendedAtUtc = null;
        _trades.Clear();
        _cumulativeR = 0m;
        _peakR = 0m;
        _losingStreak = 0;
        _firstTradeUtc = null;
    }

    private bool CheckExecution(List<string> findings)
    {
        if (_profile.AssumedSpread <= 0m || _trades.Count == 0)
        {
            return false;
        }

        var withSpread = _trades.Where(trade => trade.RealizedSpread > 0m).ToList();
        if (withSpread.Count == 0)
        {
            return false;
        }

        var averageSpread = withSpread.Average(trade => trade.RealizedSpread);
        if (averageSpread <= _profile.AssumedSpread * _thresholds.SpreadDeviationFactor)
        {
            return false;
        }

        findings.Add(string.Format(
            CultureInfo.InvariantCulture,
            "AUSFÜHRUNGSPROBLEM: realisierter Spread {0:0.####} gegenüber angenommenen {1:0.####}. " +
            "Das ist kein Strategieproblem - Broker, Handelszeit oder Symbolwahl prüfen.",
            averageSpread, _profile.AssumedSpread));

        return true;
    }

    private static HealthState Worse(HealthState left, HealthState right) => left > right ? left : right;
}
