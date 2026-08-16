using System;
using System.Collections.Generic;
using Daytrading.Strategies.Model;

namespace Daytrading.Strategies.Tests.TestSupport;

/// <summary>
/// Werkzeuge, um synthetische Marktdaten für Tests zu bauen. Bewusst simpel und
/// vollständig deterministisch - kein Zufall, keine Datei, kein Netz.
/// </summary>
internal static class TestMarket
{
    /// <summary>US-Aktiensession 13:30-20:00 UTC (9:30-16:00 New York, Sommerzeit).</summary>
    public static TradingSession UsEquitySession(DateTime dayUtc)
    {
        var day = DateTime.SpecifyKind(dayUtc.Date, DateTimeKind.Utc);
        return new TradingSession(day, day.AddHours(13).AddMinutes(30), day.AddHours(20));
    }

    /// <summary>Künstlicher Krypto-Handelstag 00:00-24:00 UTC.</summary>
    public static TradingSession CryptoSession(DateTime dayUtc)
    {
        var day = DateTime.SpecifyKind(dayUtc.Date, DateTimeKind.Utc);
        return new TradingSession(day, day, day.AddDays(1));
    }

    public static SymbolInfo Equity(string name = "TEST", decimal tickSize = 0.01m) =>
        new SymbolInfo(name, AssetClass.Equity, tickSize);

    public static DateTime Utc(int year, int month, int day, int hour = 0, int minute = 0) =>
        new DateTime(year, month, day, hour, minute, 0, DateTimeKind.Utc);

    /// <summary>
    /// Baut eine lückenlose Barfolge aus (High, Low, Close)-Tripeln. Der Open der ersten Bar
    /// ist ihr Close, danach jeweils der Close der Vorgängerbar - so, wie es eine reale
    /// zusammenhängende Serie tut.
    /// </summary>
    public static List<Candle> Series(
        DateTime startUtc,
        Timeframe timeframe,
        params (decimal High, decimal Low, decimal Close)[] bars)
    {
        var result = new List<Candle>(bars.Length);
        var time = startUtc;
        decimal? previousClose = null;

        foreach (var bar in bars)
        {
            var open = previousClose ?? bar.Close;
            open = Math.Min(bar.High, Math.Max(bar.Low, open));
            result.Add(new Candle(time, open, bar.High, bar.Low, bar.Close, 1_000m));
            previousClose = bar.Close;
            time += timeframe.Duration;
        }

        return result;
    }

    /// <summary>Einzelne Bar mit angegebenem Close und einer Spanne von +/- <paramref name="halfRange"/>.</summary>
    public static Candle Bar(DateTime openTimeUtc, decimal close, decimal halfRange = 0.5m, decimal? open = null)
    {
        var high = Math.Max(close, open ?? close) + halfRange;
        var low = Math.Min(close, open ?? close) - halfRange;
        return new Candle(openTimeUtc, open ?? close, high, low, close, 1_000m);
    }
}
