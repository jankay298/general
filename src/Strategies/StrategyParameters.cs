using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Daytrading.Strategies;

/// <summary>Fehler in der Parametrisierung einer Strategie.</summary>
public sealed class StrategyParameterException : Exception
{
    public StrategyParameterException(string message)
        : base(message)
    {
    }
}

/// <summary>
/// Typisierter Zugriff auf die Parameter einer Strategie-Instanz.
/// </summary>
/// <remarks>
/// Parameter kommen als Text aus Konfigurationsdateien oder aus cTrader-<c>[Parameter]</c>-Feldern.
/// Drei Eigenschaften sind Absicht:
/// <list type="bullet">
///   <item>Geparst wird immer mit <see cref="CultureInfo.InvariantCulture"/>. Ein Backtest darf
///         nicht davon abhängen, ob die Maschine Komma oder Punkt als Dezimaltrennzeichen nutzt.</item>
///   <item>Unlesbare Werte werfen eine Ausnahme, statt auf den Default zurückzufallen. Ein Tippfehler
///         soll auffallen und nicht 10.000 Backtest-Zeilen mit falschen Parametern erzeugen.</item>
///   <item>Jeder gelesene Wert wird mitprotokolliert - einschließlich der Defaults. Daraus entsteht
///         der <see cref="Fingerprint"/>, mit dem sich später nachweisen lässt, welche und wie viele
///         Parameterkombinationen tatsächlich getestet wurden.</item>
/// </list>
/// </remarks>
public sealed class StrategyParameters
{
    private readonly Dictionary<string, string> _supplied;
    private readonly SortedDictionary<string, string> _resolved;
    private readonly HashSet<string> _readKeys;

    public StrategyParameters(IEnumerable<KeyValuePair<string, string>>? values = null)
    {
        _supplied = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        _resolved = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        _readKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (values != null)
        {
            foreach (var pair in values)
            {
                if (string.IsNullOrWhiteSpace(pair.Key))
                {
                    throw new StrategyParameterException("Parametername darf nicht leer sein.");
                }

                _supplied[pair.Key.Trim()] = pair.Value;
            }
        }
    }

    public static StrategyParameters Empty => new StrategyParameters();

    /// <summary>Alle tatsächlich verwendeten Werte inklusive der Defaults, alphabetisch sortiert.</summary>
    public IReadOnlyDictionary<string, string> Resolved => _resolved;

    /// <summary>
    /// Übergebene Parameter, die keine Strategie gelesen hat. Fast immer ein Tippfehler
    /// im Namen - der Host soll das als Warnung melden.
    /// </summary>
    public IReadOnlyList<string> UnusedKeys =>
        _supplied.Keys.Where(key => !_readKeys.Contains(key)).OrderBy(key => key, StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>Stabile Textform der Parameterkombination, z.B. "AtrPeriod=14;TakeProfitR=2".</summary>
    public string Fingerprint => string.Join(";", _resolved.Select(pair => pair.Key + "=" + pair.Value));

    public int GetInt(string key, int fallback, int? min = null, int? max = null)
    {
        if (!TryGetRaw(key, out var raw))
        {
            return Record(key, fallback, min, max);
        }

        if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
        {
            throw new StrategyParameterException($"Parameter '{key}': '{raw}' ist keine ganze Zahl.");
        }

        return Record(key, value, min, max);
    }

    public decimal GetDecimal(string key, decimal fallback, decimal? min = null, decimal? max = null)
    {
        if (!TryGetRaw(key, out var raw))
        {
            return Record(key, fallback, min, max);
        }

        if (!decimal.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
        {
            throw new StrategyParameterException(
                $"Parameter '{key}': '{raw}' ist keine Dezimalzahl. Dezimaltrennzeichen ist der Punkt.");
        }

        return Record(key, value, min, max);
    }

    public bool GetBool(string key, bool fallback)
    {
        if (!TryGetRaw(key, out var raw))
        {
            _resolved[key] = fallback ? "true" : "false";
            return fallback;
        }

        var text = raw.Trim().ToLowerInvariant();
        bool value;
        switch (text)
        {
            case "true":
            case "1":
            case "yes":
            case "ja":
                value = true;
                break;
            case "false":
            case "0":
            case "no":
            case "nein":
                value = false;
                break;
            default:
                throw new StrategyParameterException($"Parameter '{key}': '{raw}' ist kein Wahrheitswert.");
        }

        _resolved[key] = value ? "true" : "false";
        return value;
    }

    public TEnum GetEnum<TEnum>(string key, TEnum fallback)
        where TEnum : struct
    {
        if (!TryGetRaw(key, out var raw))
        {
            _resolved[key] = fallback.ToString();
            return fallback;
        }

        if (!Enum.TryParse<TEnum>(raw.Trim(), ignoreCase: true, out var value) || !Enum.IsDefined(typeof(TEnum), value))
        {
            var allowed = string.Join(", ", Enum.GetNames(typeof(TEnum)));
            throw new StrategyParameterException(
                $"Parameter '{key}': '{raw}' ist kein gültiger Wert. Erlaubt sind: {allowed}.");
        }

        _resolved[key] = value.ToString();
        return value;
    }

    public string GetString(string key, string fallback)
    {
        var value = TryGetRaw(key, out var raw) ? raw : fallback;
        _resolved[key] = value ?? string.Empty;
        return value ?? string.Empty;
    }

    /// <summary>Zeitspanne in Minuten, z.B. für Fenster- und Haltedauerparameter.</summary>
    public TimeSpan GetMinutes(string key, int fallbackMinutes, int? min = null, int? max = null) =>
        TimeSpan.FromMinutes(GetInt(key, fallbackMinutes, min, max));

    private bool TryGetRaw(string key, out string raw)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            throw new StrategyParameterException("Parametername darf nicht leer sein.");
        }

        _readKeys.Add(key);
        if (_supplied.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value))
        {
            raw = value;
            return true;
        }

        raw = string.Empty;
        return false;
    }

    private int Record(string key, int value, int? min, int? max)
    {
        if (min.HasValue && value < min.Value)
        {
            throw new StrategyParameterException($"Parameter '{key}': {value} liegt unter dem Minimum {min.Value}.");
        }

        if (max.HasValue && value > max.Value)
        {
            throw new StrategyParameterException($"Parameter '{key}': {value} liegt über dem Maximum {max.Value}.");
        }

        _resolved[key] = value.ToString(CultureInfo.InvariantCulture);
        return value;
    }

    private decimal Record(string key, decimal value, decimal? min, decimal? max)
    {
        if (min.HasValue && value < min.Value)
        {
            throw new StrategyParameterException(
                string.Format(CultureInfo.InvariantCulture, "Parameter '{0}': {1} liegt unter dem Minimum {2}.", key, value, min.Value));
        }

        if (max.HasValue && value > max.Value)
        {
            throw new StrategyParameterException(
                string.Format(CultureInfo.InvariantCulture, "Parameter '{0}': {1} liegt über dem Maximum {2}.", key, value, max.Value));
        }

        _resolved[key] = value.ToString(CultureInfo.InvariantCulture);
        return value;
    }
}
