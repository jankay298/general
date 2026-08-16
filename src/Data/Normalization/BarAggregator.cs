using System;
using System.Collections.Generic;
using Daytrading.Strategies.Model;

namespace Daytrading.Data.Normalization;

/// <summary>
/// Fasst feine Bars zu gröberen zusammen.
/// </summary>
/// <remarks>
/// Der Grund, warum immer in der feinsten verfügbaren Auflösung gespeichert wird: Aus 1-Minuten-Bars
/// entstehen 5-, 15- oder 60-Minuten-Bars jederzeit ohne neuen Download. Der umgekehrte Weg
/// existiert nicht.
///
/// Zwei bewusste Festlegungen:
/// <list type="bullet">
///   <item>Ein Zielintervall wird nur ausgegeben, wenn mindestens eine Quellbar darin liegt.
///         Leere Intervalle bleiben leer, statt eine Bar mit dem letzten Kurs zu erfinden.</item>
///   <item>Die Zielintervalle liegen auf einem festen Raster ab Mitternacht UTC. Damit ist die
///         Aggregation unabhängig davon, wo die Serie zufällig beginnt - und deterministisch.</item>
/// </list>
/// </remarks>
public static class BarAggregator
{
    public static IReadOnlyList<Candle> Aggregate(IReadOnlyList<Candle> source, Timeframe from, Timeframe to)
    {
        if (source == null)
        {
            throw new ArgumentNullException(nameof(source));
        }

        if (to.Duration < from.Duration)
        {
            throw new ArgumentException(
                $"Aus {from} lassen sich keine {to}-Bars bilden. Feinere Auflösung entsteht nicht durch Aggregation.",
                nameof(to));
        }

        var ticksPerTarget = to.Duration.Ticks;
        if (ticksPerTarget % from.Duration.Ticks != 0)
        {
            throw new ArgumentException(
                $"{to} ist kein ganzzahliges Vielfaches von {from}. Das Ergebnis wäre nicht reproduzierbar.",
                nameof(to));
        }

        if (to == from)
        {
            return source;
        }

        var result = new List<Candle>(source.Count / (int)(ticksPerTarget / from.Duration.Ticks) + 1);

        var bucketStart = DateTime.MinValue;
        decimal open = 0m, high = 0m, low = 0m, close = 0m, volume = 0m;
        var hasBucket = false;

        for (var i = 0; i < source.Count; i++)
        {
            var bar = source[i];
            var start = FloorToGrid(bar.OpenTimeUtc, ticksPerTarget);

            if (!hasBucket)
            {
                bucketStart = start;
                open = bar.Open;
                high = bar.High;
                low = bar.Low;
                close = bar.Close;
                volume = bar.Volume;
                hasBucket = true;
                continue;
            }

            if (start != bucketStart)
            {
                result.Add(new Candle(bucketStart, open, high, low, close, volume));
                bucketStart = start;
                open = bar.Open;
                high = bar.High;
                low = bar.Low;
                close = bar.Close;
                volume = bar.Volume;
                continue;
            }

            high = Math.Max(high, bar.High);
            low = Math.Min(low, bar.Low);
            close = bar.Close;
            volume += bar.Volume;
        }

        if (hasBucket)
        {
            result.Add(new Candle(bucketStart, open, high, low, close, volume));
        }

        return result;
    }

    private static DateTime FloorToGrid(DateTime timeUtc, long ticksPerTarget)
    {
        var ticks = timeUtc.Ticks - timeUtc.Ticks % ticksPerTarget;
        return new DateTime(ticks, DateTimeKind.Utc);
    }
}
