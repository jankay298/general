using System;
using System.Globalization;
using System.Text;
using Daytrading.Strategies.Model;

namespace Daytrading.Execution;

/// <summary>
/// Ein abgeschlossener Trade. Backtester, Demo- und Livebetrieb schreiben dasselbe Format,
/// damit sich die Erwartung aus dem Backtest direkt gegen die Realität halten lässt.
/// </summary>
public sealed class TradeRecord
{
    public TradeRecord(
        string strategyKey,
        Position position,
        DateTime exitTimeUtc,
        decimal exitPrice,
        ExitReason exitReason,
        decimal grossPnL,
        decimal commission,
        decimal plannedRiskAmount,
        decimal plannedRiskPercent)
    {
        if (position == null)
        {
            throw new ArgumentNullException(nameof(position));
        }

        if (exitTimeUtc.Kind != DateTimeKind.Utc)
        {
            throw new ArgumentException("Ausstiegszeit muss UTC sein.", nameof(exitTimeUtc));
        }

        if (exitTimeUtc < position.EntryTimeUtc)
        {
            throw new ArgumentException(
                $"Ausstieg {exitTimeUtc:O} liegt vor dem Einstieg {position.EntryTimeUtc:O}.", nameof(exitTimeUtc));
        }

        StrategyKey = strategyKey ?? string.Empty;
        PositionId = position.Id;
        Symbol = position.Symbol;
        Direction = position.Direction;
        EntryTimeUtc = position.EntryTimeUtc;
        EntryPrice = position.EntryPrice;
        StopLoss = position.StopLoss;
        TakeProfit = position.TakeProfit;
        Quantity = position.Quantity;
        ExitTimeUtc = exitTimeUtc;
        ExitPrice = exitPrice;
        ExitReason = exitReason;
        GrossPnL = grossPnL;
        Commission = commission;
        PlannedRiskAmount = plannedRiskAmount;
        PlannedRiskPercent = plannedRiskPercent;
    }

    public string StrategyKey { get; }

    public string PositionId { get; }

    public string Symbol { get; }

    public TradeDirection Direction { get; }

    public DateTime EntryTimeUtc { get; }

    public decimal EntryPrice { get; }

    public decimal StopLoss { get; }

    public decimal? TakeProfit { get; }

    public decimal Quantity { get; }

    public DateTime ExitTimeUtc { get; }

    public decimal ExitPrice { get; }

    public ExitReason ExitReason { get; }

    /// <summary>Ergebnis vor Kosten.</summary>
    public decimal GrossPnL { get; }

    public decimal Commission { get; }

    /// <summary>Ergebnis nach Kosten. Nur diese Zahl zählt in der Auswertung.</summary>
    public decimal NetPnL => GrossPnL - Commission;

    /// <summary>Beim Einstieg geplanter Risikobetrag.</summary>
    public decimal PlannedRiskAmount { get; }

    public decimal PlannedRiskPercent { get; }

    public TimeSpan HoldingTime => ExitTimeUtc - EntryTimeUtc;

    /// <summary>Ergebnis in Vielfachen des geplanten Risikos. Die vergleichbarste Kennzahl über Symbole hinweg.</summary>
    public decimal RMultiple => PlannedRiskAmount > 0m ? NetPnL / PlannedRiskAmount : 0m;

    public bool IsWinner => NetPnL > 0m;
}

/// <summary>
/// CSV-Format der Trade-Logs. Gemeinsam für Backtester, Demo und Live, damit ein Vergleich
/// nicht an unterschiedlichen Spalten scheitert.
/// </summary>
public static class TradeRecordCsv
{
    public const string Header =
        "strategy;symbol;direction;entry_time_utc;entry_price;exit_time_utc;exit_price;exit_reason;" +
        "quantity;stop_loss;take_profit;holding_minutes;planned_risk_amount;planned_risk_percent;" +
        "gross_pnl;commission;net_pnl;r_multiple;position_id";

    public static string Format(TradeRecord record)
    {
        if (record == null)
        {
            throw new ArgumentNullException(nameof(record));
        }

        var builder = new StringBuilder(192);
        Append(builder, record.StrategyKey);
        Append(builder, record.Symbol);
        Append(builder, record.Direction.ToString());
        Append(builder, Time(record.EntryTimeUtc));
        Append(builder, Number(record.EntryPrice));
        Append(builder, Time(record.ExitTimeUtc));
        Append(builder, Number(record.ExitPrice));
        Append(builder, record.ExitReason.ToString());
        Append(builder, Number(record.Quantity));
        Append(builder, Number(record.StopLoss));
        Append(builder, record.TakeProfit.HasValue ? Number(record.TakeProfit.Value) : string.Empty);
        Append(builder, Number((decimal)record.HoldingTime.TotalMinutes));
        Append(builder, Number(record.PlannedRiskAmount));
        Append(builder, Number(record.PlannedRiskPercent));
        Append(builder, Number(record.GrossPnL));
        Append(builder, Number(record.Commission));
        Append(builder, Number(record.NetPnL));
        Append(builder, Number(record.RMultiple));
        builder.Append(Escape(record.PositionId));
        return builder.ToString();
    }

    private static void Append(StringBuilder builder, string value)
    {
        builder.Append(Escape(value));
        builder.Append(';');
    }

    private static string Escape(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        // Semikolon ist das Trennzeichen; Zeilenumbrüche würden den Datensatz zerreißen.
        return value.Replace(';', ',').Replace('\r', ' ').Replace('\n', ' ');
    }

    private static string Time(DateTime value) =>
        value.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    private static string Number(decimal value) =>
        value.ToString("0.##########", CultureInfo.InvariantCulture);
}
