using System;
using System.Globalization;

namespace Daytrading.Strategies.Model;

/// <summary>
/// Bargröße. Gespeichert wird immer die feinste verfügbare Auflösung; höhere Timeframes
/// entstehen im Code durch Aggregation. Der umgekehrte Weg ist nicht möglich, deshalb
/// ist der Timeframe ein expliziter Wert und keine Nebenannahme.
/// </summary>
public readonly struct Timeframe : IEquatable<Timeframe>
{
    public Timeframe(TimeSpan duration)
    {
        if (duration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(duration), duration, "Ein Timeframe muss eine positive Dauer haben.");
        }

        Duration = duration;
    }

    public TimeSpan Duration { get; }

    public static Timeframe FromMinutes(int minutes) => new Timeframe(TimeSpan.FromMinutes(minutes));

    public static Timeframe FromHours(int hours) => new Timeframe(TimeSpan.FromHours(hours));

    public static Timeframe M1 => FromMinutes(1);

    public static Timeframe M5 => FromMinutes(5);

    public static Timeframe M15 => FromMinutes(15);

    public static Timeframe M30 => FromMinutes(30);

    public static Timeframe H1 => FromHours(1);

    public static Timeframe H4 => FromHours(4);

    public static Timeframe D1 => new Timeframe(TimeSpan.FromDays(1));

    /// <summary>
    /// Liest Kurzformen wie "M15", "H1", "D1" oder "5m", "1h" aus der Konfiguration.
    /// Wirft bei unbekannter Schreibweise - stille Defaults wären hier besonders teuer.
    /// </summary>
    public static Timeframe Parse(string text)
    {
        if (!TryParse(text, out var timeframe))
        {
            throw new FormatException(
                $"'{text}' ist kein gültiger Timeframe. Erlaubt sind z.B. M1, M5, M15, H1, H4, D1 oder 15m, 1h, 1d.");
        }

        return timeframe;
    }

    public static bool TryParse(string? text, out Timeframe timeframe)
    {
        timeframe = default;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var value = text!.Trim();
        char unit;
        string number;

        if (char.IsLetter(value[0]))
        {
            unit = char.ToUpperInvariant(value[0]);
            number = value.Substring(1);
        }
        else
        {
            unit = char.ToUpperInvariant(value[value.Length - 1]);
            number = value.Substring(0, value.Length - 1);
        }

        if (!int.TryParse(number, NumberStyles.Integer, CultureInfo.InvariantCulture, out var amount) || amount <= 0)
        {
            return false;
        }

        switch (unit)
        {
            case 'S':
                timeframe = new Timeframe(TimeSpan.FromSeconds(amount));
                return true;
            case 'M':
                timeframe = FromMinutes(amount);
                return true;
            case 'H':
                timeframe = FromHours(amount);
                return true;
            case 'D':
                timeframe = new Timeframe(TimeSpan.FromDays(amount));
                return true;
            default:
                return false;
        }
    }

    public bool Equals(Timeframe other) => Duration == other.Duration;

    public override bool Equals(object? obj) => obj is Timeframe other && Equals(other);

    public override int GetHashCode() => Duration.GetHashCode();

    public static bool operator ==(Timeframe left, Timeframe right) => left.Equals(right);

    public static bool operator !=(Timeframe left, Timeframe right) => !left.Equals(right);

    /// <summary>Kanonische Kurzform, so wie sie in Dateinamen und Reports auftaucht.</summary>
    public override string ToString()
    {
        var total = Duration;
        if (total.TotalDays >= 1 && total.TotalDays % 1 == 0)
        {
            return "D" + ((int)total.TotalDays).ToString(CultureInfo.InvariantCulture);
        }

        if (total.TotalHours >= 1 && total.TotalHours % 1 == 0)
        {
            return "H" + ((int)total.TotalHours).ToString(CultureInfo.InvariantCulture);
        }

        if (total.TotalMinutes >= 1 && total.TotalMinutes % 1 == 0)
        {
            return "M" + ((int)total.TotalMinutes).ToString(CultureInfo.InvariantCulture);
        }

        return "S" + ((int)total.TotalSeconds).ToString(CultureInfo.InvariantCulture);
    }
}
