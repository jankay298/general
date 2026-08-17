using System;
using Daytrading.Strategies.Model;

namespace Daytrading.Execution;

/// <summary>
/// Zieht den Stop einer laufenden Position nach.
/// </summary>
/// <remarks>
/// Gehört in die Ausführungsschicht und nicht in die Strategien: So wirkt dieselbe Regel auf
/// alle, und die Frage "hilft Nachziehen überhaupt?" ist einmal zu beantworten statt sechsmal.
///
/// Beide Verfahren verschieben nur die <em>Verteilung</em> der Ergebnisse, nicht ihren
/// Mittelwert - jedenfalls nicht von selbst. Ein Break-even-Stop verwandelt kleine Verluste in
/// Nullnummern und schneidet dafür Trades ab, die nach einem Rücksetzer noch ins Ziel gelaufen
/// wären. Ob dieser Tausch sich lohnt, hängt daran, wie oft ein Kurs nach dem Erreichen von
/// +1R noch einmal bis zum Einstieg zurückkommt und dann doch dreht. Das ist eine Messfrage und
/// keine Glaubensfrage, und die verbreitete Überzeugung, Nachziehen sei "immer richtig", ist
/// vor allem das: verbreitet.
///
/// Der Stop bewegt sich ausschließlich in Richtung des Gewinns. Ein Stop, der sich vom Kurs
/// entfernen dürfte, wäre kein Stop mehr, sondern ein Hoffen mit zusätzlichen Schritten.
/// </remarks>
public static class StopManagement
{
    /// <summary>
    /// Neuer Stop für eine laufende Position, oder null wenn er unverändert bleibt.
    /// </summary>
    /// <param name="position">Die Position mit ihrem aktuell gültigen Stop.</param>
    /// <param name="initialRiskDistance">
    /// Der Abstand Einstieg-zu-Stop <b>beim Eröffnen</b>. Er ist die Bezugsgröße für R und darf
    /// nicht mitwandern, sonst würde jedes Nachziehen die Messlatte mitverschieben.
    /// </param>
    /// <param name="bestPrice">Bestkurs seit Einstieg: höchstes Hoch bei Long, tiefstes Tief bei Short.</param>
    public static decimal? Adjust(
        Position position,
        decimal initialRiskDistance,
        decimal bestPrice,
        RiskLimits limits)
    {
        if (position == null)
        {
            throw new ArgumentNullException(nameof(position));
        }

        if (limits == null)
        {
            throw new ArgumentNullException(nameof(limits));
        }

        if (initialRiskDistance <= 0m)
        {
            return null;
        }

        var sign = position.Direction.Sign();
        var progressR = (bestPrice - position.EntryPrice) * sign / initialRiskDistance;
        var candidate = position.StopLoss;

        // Break-even: ab dem eingestellten Vorsprung sitzt der Stop auf dem Einstieg.
        if (limits.BreakevenAfterR > 0m && progressR >= limits.BreakevenAfterR)
        {
            candidate = Better(candidate, position.EntryPrice, sign);
        }

        // Trailing: fester Abstand hinter dem Bestkurs, gemessen im ursprünglichen Risiko.
        if (limits.TrailStopDistanceR > 0m && progressR >= limits.TrailStartsAfterR)
        {
            candidate = Better(candidate, bestPrice - (initialRiskDistance * limits.TrailStopDistanceR * sign), sign);
        }

        return candidate == position.StopLoss ? (decimal?)null : candidate;
    }

    /// <summary>Der für den Halter günstigere der beiden Stops - also der näher am Kurs liegende.</summary>
    private static decimal Better(decimal current, decimal proposed, int sign) =>
        (proposed - current) * sign > 0m ? proposed : current;
}
