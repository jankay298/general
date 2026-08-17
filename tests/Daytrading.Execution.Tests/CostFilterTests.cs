using Daytrading.Execution.Tests.TestSupport;

namespace Daytrading.Execution.Tests;

/// <summary>
/// Der Kostenfilter: Trades ablehnen, deren Stop zu eng für den Spread ist.
/// </summary>
/// <remarks>
/// Der Hintergrund ist gemessen, nicht vermutet. Über 720 Kombinationen lag der
/// Erwartungswert im Median bei -0.31 R, und die Gegenprobe mit gespiegelten Signalen zeigte,
/// dass es nicht an der Richtung liegt: Beide Richtungen zusammen ergaben -0.59 R. Was beide
/// Richtungen gleichermaßen kostet, sind Spread und Kommission.
///
/// Ein enger Stop wirkt sparsam, ist aber das Gegenteil: Die Kosten bleiben gleich, das
/// Risiko schrumpft - der Anteil, der schon vor der ersten Kursbewegung weg ist, wächst.
/// </remarks>
public class CostFilterTests
{
    private static ExecutionSymbol SymbolWithSpread(decimal spread, decimal commissionPerSide = 0m) =>
        Fixture.Symbol(typicalSpread: spread, commissionPerUnitPerSide: commissionPerSide);

    private static RiskLimits LimitsWithCostCap(decimal percent)
    {
        var limits = Fixture.Limits();
        limits.MaxCostShareOfRiskPercent = percent;
        return limits;
    }

    private static RiskDecision Decide(ExecutionSymbol symbol, RiskLimits limits, decimal stop) =>
        new RiskGate(symbol, limits).Evaluate(
            Fixture.LongEntry(price: 100m, stop: stop),
            Fixture.Snapshot(Fixture.At(15, 0), 100m, symbol: symbol),
            Fixture.Account());

    [Fact]
    public void Rejects_an_entry_whose_stop_is_too_tight_for_the_spread()
    {
        // Spread 0.40, Stopabstand 0.50: 80 % des Risikos sind Kosten.
        var decision = Decide(SymbolWithSpread(0.40m), LimitsWithCostCap(25m), 99.5m);

        Assert.False(decision.Accepted);
        Assert.Equal(RiskRejectionReason.CostTooHighForStopDistance, decision.Reason);
    }

    [Fact]
    public void Accepts_the_same_signal_when_the_stop_is_wide_enough()
    {
        // Derselbe Spread, aber Stopabstand 4.00: die Kosten sind 10 % des Risikos.
        var decision = Decide(SymbolWithSpread(0.40m), LimitsWithCostCap(25m), 96m);

        Assert.True(decision.Accepted);
    }

    [Fact]
    public void Counts_the_commission_for_both_sides_of_the_trade()
    {
        // Kein Spread, aber 0.30 Kommission je Seite und Einheit: 0.60 auf 2.00 Risiko = 30 %.
        var symbol = SymbolWithSpread(0m, commissionPerSide: 0.30m);

        Assert.False(Decide(symbol, LimitsWithCostCap(25m), 98m).Accepted);
        Assert.True(Decide(symbol, LimitsWithCostCap(35m), 98m).Accepted);
    }

    [Fact]
    public void Is_off_by_default_so_existing_configurations_behave_as_before()
    {
        var limits = Fixture.Limits();

        Assert.Equal(100m, limits.MaxCostShareOfRiskPercent);
        Assert.True(Decide(SymbolWithSpread(0.40m), limits, 99.5m).Accepted);
    }

    [Fact]
    public void Adapts_to_the_instrument_instead_of_fixing_a_distance_in_points()
    {
        // Derselbe Grenzwert, zwei Instrumente: Beim teureren verlangt er einen weiteren Stop.
        // Eine feste Mindestweite in Punkten koennte das nicht leisten.
        var cheap = SymbolWithSpread(0.10m);
        var expensive = SymbolWithSpread(1.00m);
        var limits = LimitsWithCostCap(20m);

        Assert.True(Decide(cheap, limits, 99m).Accepted);      // 0.10 auf 1.00 = 10 %
        Assert.False(Decide(expensive, limits, 99m).Accepted); // 1.00 auf 1.00 = 100 %
        Assert.True(Decide(expensive, limits, 94m).Accepted);  // 1.00 auf 6.00 = 17 %
    }
}
