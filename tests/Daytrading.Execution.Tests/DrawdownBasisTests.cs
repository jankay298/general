using Daytrading.Execution.Tests.TestSupport;

namespace Daytrading.Execution.Tests;

/// <summary>
/// Die beiden Bezugsgrößen des Gesamtrückgangs.
/// </summary>
/// <remarks>
/// Beide heißen "10 % Drawdown" und meinen etwas anderes, sobald das Konto im Gewinn steht.
/// Wer mit fremdem Kapital handelt, hat die Bezugsgröße nicht frei gewählt - sie steht im
/// Vertrag, und eine zu strenge Einstellung schaltet den Bot ab, obwohl das Konto gesund ist.
/// </remarks>
public class DrawdownBasisTests
{
    /// <summary>
    /// Konto, das sein Hoch an einem frueheren Tag gesehen hat. Der Tageswechsel ist wichtig:
    /// Sonst schlaegt die Tagesgrenze zu und der Test misst gar nicht die Gesamtregel.
    /// </summary>
    private static AccountState AccountAt(decimal peak, decimal equity)
    {
        var account = new AccountState(100_000m);
        account.BeginTradingDay(Fixture.Day);
        account.UpdateEquity(peak);
        account.UpdateEquity(equity);

        // Erst der Tageswechsel setzt das Tageshoch auf den aktuellen Stand zurueck. Ohne ihn
        // meldet das Konto den ganzen Rueckgang als Tagesrueckgang, und der Test wuerde an der
        // Tagesgrenze scheitern statt die Gesamtregel zu pruefen.
        account.BeginTradingDay(Fixture.Day.AddDays(1));
        return account;
    }

    [Fact]
    public void The_trailing_rule_counts_a_fall_from_the_high_even_when_the_account_is_in_profit()
    {
        // Hoch 110.000, jetzt 101.000: acht Prozent unter dem Hoch, aber tausend im Plus.
        var account = AccountAt(110_000m, 101_000m);

        Assert.Equal(8.18m, account.TotalDrawdownPercentFor(DrawdownBasis.TrailingPeak), 2);
        Assert.Equal(0m, account.TotalDrawdownPercentFor(DrawdownBasis.InitialBalance));
    }

    [Fact]
    public void The_fixed_threshold_only_counts_what_is_below_the_starting_balance()
    {
        // Genau der Fall eines Kontos mit fester Schwelle: 100.000 Start, Schluss bei 90.000.
        var account = AccountAt(110_000m, 90_000m);

        Assert.Equal(10m, account.TotalDrawdownPercentFor(DrawdownBasis.InitialBalance));
        Assert.Equal(18.18m, account.TotalDrawdownPercentFor(DrawdownBasis.TrailingPeak), 2);
    }

    [Fact]
    public void Both_rules_agree_while_the_account_has_never_been_in_profit()
    {
        var account = AccountAt(100_000m, 95_000m);

        Assert.Equal(5m, account.TotalDrawdownPercentFor(DrawdownBasis.TrailingPeak));
        Assert.Equal(5m, account.TotalDrawdownPercentFor(DrawdownBasis.InitialBalance));
    }

    [Fact]
    public void The_gate_stops_a_profitable_account_under_the_trailing_rule_but_not_under_the_fixed_one()
    {
        // Dieselbe Kontolage, zwei Regeln, zwei Entscheidungen. Genau hier lag der Unterschied
        // zwischen "Bot steht" und "Bot handelt weiter".
        var account = AccountAt(110_000m, 98_000m);

        var trailing = Fixture.Limits();
        trailing.MaxTotalDrawdownPercent = 10m;
        trailing.TotalDrawdownBasis = DrawdownBasis.TrailingPeak;

        var fixedThreshold = Fixture.Limits();
        fixedThreshold.MaxTotalDrawdownPercent = 10m;
        fixedThreshold.TotalDrawdownBasis = DrawdownBasis.InitialBalance;

        Assert.False(Decide(trailing, account).Accepted);
        Assert.True(Decide(fixedThreshold, account).Accepted);
    }

    private static RiskDecision Decide(RiskLimits limits, AccountState account) =>
        new RiskGate(Fixture.Symbol(), limits)
            .Evaluate(Fixture.LongEntry(), Fixture.Snapshot(Fixture.At(15, 0), 100m), account);
}
