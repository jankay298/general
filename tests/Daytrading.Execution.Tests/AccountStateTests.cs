using System;
using Daytrading.Execution;
using Daytrading.Execution.Tests.TestSupport;

namespace Daytrading.Execution.Tests;

public class AccountStateTests
{
    [Fact]
    public void Starts_flat_with_no_drawdown()
    {
        var account = Fixture.Account();

        Assert.Equal(10_000m, account.Balance);
        Assert.Equal(10_000m, account.Equity);
        Assert.Equal(0m, account.DailyLossPercent);
        Assert.Equal(0m, account.DailyDrawdownPercent);
        Assert.Equal(0m, account.TotalDrawdownPercent);
    }

    [Fact]
    public void A_winning_day_never_reports_a_loss()
    {
        var account = Fixture.Account();

        account.ApplyRealizedPnL(500m);

        Assert.Equal(0m, account.DailyLossPercent);
        Assert.Equal(0m, account.DailyDrawdownPercent);
        Assert.Equal(0m, account.TotalDrawdownPercent);
    }

    [Fact]
    public void Daily_drawdown_measures_from_the_day_high_not_from_the_day_start()
    {
        // Der Fall, der Tagesverlust und Tages-Drawdown unterscheidet: +3 % und zurück auf -1 %.
        // Realisiert fehlt 1 %, vom Tageshoch aus sind es 4 %.
        var account = Fixture.Account();
        account.BeginTradingDay(Fixture.Day);

        account.ApplyRealizedPnL(300m);   // Equity 10.300, Tageshoch 10.300
        account.ApplyRealizedPnL(-400m);  // Equity 9.900

        Assert.Equal(1m, account.DailyLossPercent);
        Assert.Equal(4m, account.DailyDrawdownPercent);
    }

    [Fact]
    public void Unrealized_losses_count_towards_the_daily_drawdown_but_not_the_daily_loss()
    {
        var account = Fixture.Account();
        account.BeginTradingDay(Fixture.Day);

        account.UpdateEquity(9_800m);

        Assert.Equal(0m, account.DailyLossPercent);
        Assert.Equal(2m, account.DailyDrawdownPercent);
    }

    [Fact]
    public void Total_drawdown_measures_from_the_all_time_equity_high()
    {
        var account = Fixture.Account();
        account.BeginTradingDay(Fixture.Day);
        account.ApplyRealizedPnL(1_000m);  // Hoch bei 11.000

        account.BeginTradingDay(Fixture.Day.AddDays(1));
        account.ApplyRealizedPnL(-1_100m); // Equity 9.900

        Assert.Equal(10m, account.DailyLossPercent);                 // 1.100 von 11.000 Tagesbasis
        Assert.Equal(10m, account.TotalDrawdownPercent);             // 1.100 von 11.000 Allzeithoch
    }

    [Fact]
    public void Beginning_a_new_day_resets_day_basis_high_and_trade_count()
    {
        var account = Fixture.Account();
        account.BeginTradingDay(Fixture.Day);
        account.RegisterTradeOpened();
        account.RegisterTradeOpened();
        account.ApplyRealizedPnL(-200m);

        account.BeginTradingDay(Fixture.Day.AddDays(1));

        Assert.Equal(0, account.TradesToday);
        Assert.Equal(9_800m, account.DayStartBalance);
        Assert.Equal(0m, account.DailyLossPercent);
        Assert.Equal(0m, account.DailyDrawdownPercent);
        Assert.Equal(2m, account.TotalDrawdownPercent);  // Gesamt-Drawdown bleibt bestehen
    }

    [Fact]
    public void Repeating_the_same_day_changes_nothing()
    {
        var account = Fixture.Account();
        account.BeginTradingDay(Fixture.Day);
        account.RegisterTradeOpened();

        account.BeginTradingDay(Fixture.Day);

        Assert.Equal(1, account.TradesToday);
    }

    [Fact]
    public void Rejects_days_running_backwards_and_non_utc_days()
    {
        var account = Fixture.Account();
        account.BeginTradingDay(Fixture.Day);

        Assert.Throws<InvalidOperationException>(() => account.BeginTradingDay(Fixture.Day.AddDays(-1)));
        Assert.Throws<ArgumentException>(
            () => account.BeginTradingDay(DateTime.SpecifyKind(Fixture.Day.AddDays(1), DateTimeKind.Local)));
    }

    [Fact]
    public void Rejects_a_non_positive_starting_balance()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new AccountState(0m));
    }
}
