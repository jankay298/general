using System;
using System.Collections.Generic;
using Daytrading.Execution;
using Daytrading.Strategies.Model;

namespace Daytrading.Backtester;

/// <summary>Einstellungen eines Backtest-Laufs.</summary>
public sealed class BacktestOptions
{
    public decimal StartingBalance { get; set; } = 10_000m;

    /// <summary>Slippage einer Marktorder in Ticks.</summary>
    public decimal SlippageTicks { get; set; } = 1m;

    /// <summary>Slippage einer Stop-Order in Ticks. Höher, weil sie in die Bewegung hinein auslöst.</summary>
    public decimal StopSlippageTicks { get; set; } = 3m;

    /// <summary>Fester Seed. Gleicher Input muss gleichen Output ergeben.</summary>
    public int RandomSeed { get; set; } = 20240301;

    /// <summary>Mindestanzahl Trades, ab der ein Ergebnis als statistisch belastbar gilt.</summary>
    public int MinimumSampleTrades { get; set; } = 30;

    public BacktestOptions Clone() => (BacktestOptions)MemberwiseClone();
}

/// <summary>Ergebnis eines einzelnen Laufs: Strategie × Symbol × Zeitraum × Parameterkombination.</summary>
public sealed class BacktestResult
{
    public string StrategyKey { get; init; } = string.Empty;

    public string ParameterFingerprint { get; init; } = string.Empty;

    public string Symbol { get; init; } = string.Empty;

    public Timeframe Timeframe { get; init; }

    public DateTime FromUtc { get; init; }

    public DateTime ToUtc { get; init; }

    public decimal StartingBalance { get; init; }

    public decimal FinalBalance { get; init; }

    public IReadOnlyList<TradeRecord> Trades { get; init; } = Array.Empty<TradeRecord>();

    public IReadOnlyList<EquityPoint> EquityCurve { get; init; } = Array.Empty<EquityPoint>();

    public PerformanceMetrics Metrics { get; init; } = PerformanceMetrics.Empty;

    /// <summary>Wie oft die Risikoschicht ein Signal abgelehnt hat, nach Grund. Sagt oft mehr als die Trades selbst.</summary>
    public IReadOnlyDictionary<RiskRejectionReason, int> Rejections { get; init; } =
        new Dictionary<RiskRejectionReason, int>();

    /// <summary>
    /// Zeitpunkt, zu dem der Lauf vorzeitig endete, weil eine Risikogrenze die Strategie
    /// gestoppt hat. Null, wenn er bis zum Ende der Daten lief.
    /// </summary>
    /// <remarks>
    /// Der Gesamt-Drawdown ist kein Tageslimit: Ist er einmal erreicht, verlangen die Regeln,
    /// die Strategie zu stoppen und zu prüfen. Der Lauf danach weiterzurechnen würde Jahre ohne
    /// Trades in die Kennzahlen mischen - Sharpe, Drawdown und Trades pro Woche bezögen sich auf
    /// einen Zeitraum, in dem die Strategie längst abgeschaltet war.
    /// </remarks>
    public DateTime? StoppedAtUtc { get; init; }

    public string? StopReason { get; init; }

    /// <summary>Tage, an denen der Lauf tatsächlich handeln durfte.</summary>
    public int ActiveDays { get; init; }

    public int BarsProcessed { get; init; }

    /// <summary>Bars, die in keiner Session lagen - vorbörslich, nachbörslich, Feiertag.</summary>
    public int BarsOutsideSession { get; init; }

    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();

    /// <summary>Kurzform für Log und Konsole.</summary>
    public override string ToString() =>
        $"{StrategyKey} / {Symbol}: {Metrics.TradeCount} Trades, netto {Metrics.NetPnL:0.00}, " +
        $"MaxDD {Metrics.MaxDrawdownPercent:0.00} %";
}
