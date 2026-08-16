using System;
using System.Collections.Generic;
using Daytrading.Data.Config;
using Daytrading.Strategies.Model;

namespace Daytrading.Backtester;

/// <summary>
/// Erzeugt synthetische, aber plausible Kursdaten.
/// </summary>
/// <remarks>
/// Zweck ist ausschließlich, die Kette aus Strategie, Risikoschicht, Kostenmodell und
/// Auswertung ohne Download und ohne Brokerkonto End-to-End laufen zu lassen. Ergebnisse auf
/// diesen Daten sagen <b>nichts</b> über eine Strategie aus - sie sagen nur, dass die Maschinerie
/// funktioniert.
///
/// Der Zufallsgenerator ist bewusst selbst implementiert: <see cref="Random"/> garantiert über
/// .NET-Versionen hinweg keine identische Folge, und ein Backtest, dessen Eingabedaten sich mit
/// dem Laufzeitupdate ändern, wäre nicht reproduzierbar.
/// </remarks>
public static class SyntheticMarket
{
    private sealed class Xorshift
    {
        private uint _state;

        public Xorshift(int seed)
        {
            _state = seed == 0 ? 2463534242u : unchecked((uint)seed);
        }

        public double NextUnit()
        {
            _state ^= _state << 13;
            _state ^= _state >> 17;
            _state ^= _state << 5;
            return _state / (double)uint.MaxValue;
        }

        /// <summary>Standardnormalverteilte Zufallszahl über die Box-Muller-Transformation.</summary>
        public double NextGaussian()
        {
            var u1 = Math.Max(NextUnit(), 1e-12);
            var u2 = NextUnit();
            return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
        }
    }

    public static IReadOnlyList<Candle> Generate(
        SymbolConfig config,
        DateTime fromUtc,
        DateTime toUtc,
        int seed = 20240301,
        decimal startPrice = 100m,
        decimal barVolatilityPercent = 0.15m)
    {
        if (config == null)
        {
            throw new ArgumentNullException(nameof(config));
        }

        var calendar = new SymbolSessionCalendar(config);
        var timeframe = config.ToTimeframe();
        var random = new Xorshift(seed);
        var bars = new List<Candle>();
        var price = startPrice;

        foreach (var session in calendar.Sessions(fromUtc, toUtc))
        {
            // Jeder Handelstag beginnt mit einer kleinen Eröffnungslücke - sonst gäbe es keine
            // Overnight-Effekte, und genau die machen Aktien und Indizes aus.
            price *= 1m + (decimal)(random.NextGaussian() * 0.002);

            for (var time = session.StartUtc; time + timeframe.Duration <= session.EndUtc; time += timeframe.Duration)
            {
                if (time < fromUtc || time >= toUtc)
                {
                    continue;
                }

                var open = price;
                var step = (decimal)(random.NextGaussian() * (double)(barVolatilityPercent / 100m));
                var close = open * (1m + step);
                var wick = Math.Abs(close - open) + open * barVolatilityPercent / 200m;

                var high = Math.Max(open, close) + wick * (decimal)random.NextUnit();
                var low = Math.Min(open, close) - wick * (decimal)random.NextUnit();
                var volume = 500m + (decimal)(random.NextUnit() * 1000);

                bars.Add(new Candle(
                    time,
                    Round(config, open),
                    Round(config, high),
                    Round(config, low),
                    Round(config, close),
                    Math.Round(volume, 2)));

                price = close;
            }
        }

        return bars;
    }

    private static decimal Round(SymbolConfig config, decimal price) =>
        Math.Round(price / config.TickSize, MidpointRounding.AwayFromZero) * config.TickSize;
}
