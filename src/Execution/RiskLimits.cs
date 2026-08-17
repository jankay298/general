using System;
using System.Globalization;

namespace Daytrading.Execution;

/// <summary>Fehlerhafte Risikokonfiguration.</summary>
public sealed class RiskConfigurationException : Exception
{
    public RiskConfigurationException(string message)
        : base(message)
    {
    }
}

/// <summary>
/// Die harten Grenzen der Ausführungsschicht, gültig je Handelskonto und damit je
/// Strategie-Instanz. Entspricht <c>config/risk.defaults.json</c>.
/// </summary>
/// <remarks>
/// Alle Prozentangaben beziehen sich auf den <b>Kontostand bei Tagesbeginn</b>. Ein einziger
/// Bezugswert für den ganzen Handelstag ist Absicht: Sonst hinge die erlaubte Positionsgröße
/// davon ab, ob gerade ein Gewinn offen ist, und die Tagesgrenzen ließen sich durch
/// zwischenzeitliche Buchgewinne aufweichen. Über Tage hinweg wächst das Risiko trotzdem mit
/// dem Konto, weil jeder Handelstag mit dem aktuellen Stand neu ansetzt.
/// </remarks>
public sealed class RiskLimits
{
    /// <summary>Risiko eines einzelnen Trades bis zu seinem Stop. Obergrenze, kein Zielwert.</summary>
    public decimal RiskPerTradePercent { get; set; } = 1.0m;

    /// <summary>
    /// Summe des gleichzeitig offenen Risikos. Zusammen mit dem bereits eingetretenen
    /// Tagesrückgang die eigentliche Bremse für parallele Positionen.
    /// </summary>
    public decimal MaxOpenRiskPercent { get; set; } = 4.0m;

    /// <summary>Realisierter Tagesverlust, gemessen am Kontostand seit Tagesbeginn.</summary>
    public decimal MaxDailyLossPercent { get; set; } = 4.0m;

    /// <summary>Rückgang vom höchsten Equity-Stand des laufenden Tages, realisiert und unrealisiert.</summary>
    public decimal MaxDailyDrawdownPercent { get; set; } = 4.0m;

    /// <summary>Größter zulässiger Gesamtrückgang. Danach wird die Strategie gestoppt.</summary>
    public decimal MaxTotalDrawdownPercent { get; set; } = 10.0m;

    /// <summary>Wovon der Gesamtrückgang gemessen wird.</summary>
    public DrawdownBasis TotalDrawdownBasis { get; set; } = DrawdownBasis.TrailingPeak;

    /// <summary>
    /// Reines Sicherheitsnetz gegen viele Kleinstpositionen. Die bindende Grenze für parallele
    /// Trades ist <see cref="MaxOpenRiskPercent"/>, nicht diese Zahl.
    /// </summary>
    public int MaxConcurrentPositions { get; set; } = 5;

    public int MaxTradesPerDay { get; set; } = 6;

    /// <summary>
    /// Wenn true, wird das Risiko des nächsten Trades auf das verbleibende Tagesbudget
    /// gekürzt. Wenn false, wird ein Trade, der nicht mehr vollständig ins Budget passt,
    /// abgelehnt. Gekürzt wird nie über <see cref="RiskPerTradePercent"/> hinaus.
    /// </summary>
    public bool CapRiskToRemainingDailyBudget { get; set; } = true;

    /// <summary>Zwangsschließung aller Positionen so viele Minuten vor Sessionende.</summary>
    public int ForceFlatMinutesBeforeSessionEnd { get; set; } = 15;

    /// <summary>Optionale maximale Haltedauer je Trade in Minuten. Null bedeutet: keine Grenze.</summary>
    public int? MaxHoldingMinutes { get; set; }

    // Bewusst NICHT konfigurierbar, weil es keine Einstellungen sind, sondern Regeln:
    // Pflicht-Stop-Loss, keine Position über Nacht, kein Nachkaufen in Verlustpositionen.
    // Ein Schalter dafür wäre genau die Lücke, die diese Schicht schließen soll - eine falsch
    // gesetzte Konfigurationszeile könnte sonst abschalten, was das Framework garantiert.

    public static RiskLimits Default => new RiskLimits();

    /// <summary>
    /// Prüft die Konfiguration auf Widersprüche. Wird beim Start eines Backtests oder einer
    /// cBot-Instanz aufgerufen - eine unsinnige Grenze soll sofort auffallen und nicht erst,
    /// wenn sie nie greift.
    /// </summary>
    public void Validate()
    {
        RequirePositivePercent(RiskPerTradePercent, nameof(RiskPerTradePercent));
        RequirePositivePercent(MaxOpenRiskPercent, nameof(MaxOpenRiskPercent));
        RequirePositivePercent(MaxDailyLossPercent, nameof(MaxDailyLossPercent));
        RequirePositivePercent(MaxDailyDrawdownPercent, nameof(MaxDailyDrawdownPercent));
        RequirePositivePercent(MaxTotalDrawdownPercent, nameof(MaxTotalDrawdownPercent));

        if (MaxConcurrentPositions < 1)
        {
            throw new RiskConfigurationException(
                $"MaxConcurrentPositions muss mindestens 1 sein, war {MaxConcurrentPositions}.");
        }

        if (MaxTradesPerDay < 1)
        {
            throw new RiskConfigurationException(
                $"MaxTradesPerDay muss mindestens 1 sein, war {MaxTradesPerDay}.");
        }

        if (ForceFlatMinutesBeforeSessionEnd < 0)
        {
            throw new RiskConfigurationException(
                $"ForceFlatMinutesBeforeSessionEnd darf nicht negativ sein, war {ForceFlatMinutesBeforeSessionEnd}.");
        }

        if (MaxHoldingMinutes.HasValue && MaxHoldingMinutes.Value < 1)
        {
            throw new RiskConfigurationException(
                $"MaxHoldingMinutes muss mindestens 1 sein oder null, war {MaxHoldingMinutes.Value}.");
        }

        if (RiskPerTradePercent > MaxOpenRiskPercent)
        {
            throw new RiskConfigurationException(Format(
                "RiskPerTradePercent ({0}%) ist größer als MaxOpenRiskPercent ({1}%). Kein einziger Trade " +
                "würde die Risikoprüfung je bestehen.",
                RiskPerTradePercent, MaxOpenRiskPercent));
        }

        if (MaxOpenRiskPercent > MaxDailyDrawdownPercent)
        {
            throw new RiskConfigurationException(Format(
                "MaxOpenRiskPercent ({0}%) ist größer als MaxDailyDrawdownPercent ({1}%). Die Tagesgrenze " +
                "greift dann immer zuerst und das offene Risiko wäre eine Grenze ohne Wirkung.",
                MaxOpenRiskPercent, MaxDailyDrawdownPercent));
        }

        if (ForceFlatMinutesBeforeSessionEnd == 0)
        {
            throw new RiskConfigurationException(
                "ForceFlatMinutesBeforeSessionEnd auf 0 hieße, exakt zum Sessionende schließen zu wollen. " +
                "Positionen müssen vor Sessionende geschlossen sein, also braucht es einen Vorlauf.");
        }
    }

    private static void RequirePositivePercent(decimal value, string name)
    {
        if (value <= 0m || value > 100m)
        {
            throw new RiskConfigurationException(Format(
                "{0} muss größer als 0 und höchstens 100 sein, war {1}.", name, value));
        }
    }

    private static string Format(string template, params object[] args) =>
        string.Format(CultureInfo.InvariantCulture, template, args);
}
