using System.Globalization;
using Daytrading.Execution;
using Daytrading.Execution.Tests.TestSupport;
using Daytrading.Strategies.Model;

namespace Daytrading.Execution.Tests;

public class TradeRecordTests
{
    private static TradeRecord Record(
        decimal grossPnL = 200m,
        decimal commission = 5m,
        decimal plannedRisk = 100m,
        ExitReason exitReason = ExitReason.TakeProfit)
    {
        var position = Fixture.Position(entryPrice: 100m, stopLoss: 98m, quantity: 50m, entryTimeUtc: Fixture.At(15, 0));
        return new TradeRecord(
            "OpeningRangeBreakout@1.0.0",
            position,
            Fixture.At(16, 30),
            104m,
            exitReason,
            grossPnL,
            commission,
            plannedRisk,
            1m);
    }

    [Fact]
    public void Nets_the_result_and_expresses_it_in_multiples_of_the_planned_risk()
    {
        var record = Record(grossPnL: 200m, commission: 5m, plannedRisk: 100m);

        Assert.Equal(195m, record.NetPnL);
        Assert.Equal(1.95m, record.RMultiple);
        Assert.True(record.IsWinner);
        Assert.Equal(TimeSpan.FromMinutes(90), record.HoldingTime);
    }

    [Fact]
    public void A_loss_after_costs_counts_as_a_loss()
    {
        var record = Record(grossPnL: 3m, commission: 5m);

        Assert.Equal(-2m, record.NetPnL);
        Assert.False(record.IsWinner);
    }

    [Fact]
    public void Rejects_an_exit_before_the_entry()
    {
        var position = Fixture.Position(entryTimeUtc: Fixture.At(16, 0));

        Assert.Throws<ArgumentException>(() => new TradeRecord(
            "S", position, Fixture.At(15, 0), 100m, ExitReason.StopLoss, 0m, 0m, 100m, 1m));
    }

    [Fact]
    public void Rejects_a_non_utc_exit_time()
    {
        var position = Fixture.Position();

        Assert.Throws<ArgumentException>(() => new TradeRecord(
            "S", position, DateTime.SpecifyKind(Fixture.At(16, 0), DateTimeKind.Local), 100m,
            ExitReason.StopLoss, 0m, 0m, 100m, 1m));
    }

    [Fact]
    public void Csv_row_matches_the_header_column_for_column()
    {
        var header = TradeRecordCsv.Header.Split(';');
        var row = TradeRecordCsv.Format(Record()).Split(';');

        Assert.Equal(header.Length, row.Length);
    }

    [Fact]
    public void Csv_holds_the_facts_a_trade_log_has_to_carry()
    {
        var row = TradeRecordCsv.Format(Record(exitReason: ExitReason.SessionEnd));

        Assert.Contains("OpeningRangeBreakout@1.0.0", row);
        Assert.Contains("TEST", row);
        Assert.Contains("Long", row);
        Assert.Contains("2024-03-01T15:00:00Z", row);
        Assert.Contains("2024-03-01T16:30:00Z", row);
        Assert.Contains("SessionEnd", row);
        Assert.Contains("195", row);
    }

    [Fact]
    public void Csv_stays_invariant_under_a_german_locale()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            var row = TradeRecordCsv.Format(Record(grossPnL: 200.5m, commission: 0.25m));

            // Dezimaltrennzeichen bleibt der Punkt, sonst zerlegt jedes Auswertungswerkzeug die Datei falsch.
            Assert.Contains("200.5", row);
            Assert.DoesNotContain("200,5", row);
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public void Csv_neutralises_separators_inside_text_fields()
    {
        var position = Fixture.Position(id: "A;B");
        var record = new TradeRecord(
            "Strategie;mit;Semikolon", position, Fixture.At(16, 0), 100m, ExitReason.StopLoss, 0m, 0m, 100m, 1m);

        var row = TradeRecordCsv.Format(record);

        Assert.Equal(TradeRecordCsv.Header.Split(';').Length, row.Split(';').Length);
        Assert.Contains("Strategie,mit,Semikolon", row);
    }
}
