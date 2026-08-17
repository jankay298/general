namespace Daytrading.Execution;

/// <summary>
/// Wovon der Gesamtrückgang gemessen wird. Die Wahl entscheidet, wann ein Konto stillgelegt wird.
/// </summary>
/// <remarks>
/// Beide Regeln heißen "10 % Drawdown" und meinen etwas völlig anderes, sobald das Konto im
/// Gewinn steht. Bei 100.000 Start und einem zwischenzeitlichen Hoch von 110.000 greift
/// <see cref="TrailingPeak"/> schon bei 99.000, <see cref="InitialBalance"/> erst bei 90.000 -
/// elf Prozent des Kontos Unterschied.
///
/// Es gibt keine allgemein richtige Wahl: Die eine schützt erreichte Gewinne, die andere bildet
/// die Regel vieler Kapitalgeber ab, die eine feste Schwelle unter dem Startbetrag setzen. Wer
/// mit fremdem Kapital handelt, nimmt die Regel des Kapitalgebers - eine strengere Einstellung
/// schaltet grundlos ab, eine mildere kostet das Konto.
/// </remarks>
public enum DrawdownBasis
{
    /// <summary>
    /// Rückgang vom höchsten je erreichten Equity-Stand. Die vorsichtige Fassung: Sie gibt
    /// erreichte Gewinne nicht wieder her.
    /// </summary>
    TrailingPeak = 0,

    /// <summary>
    /// Rückgang unter das Startkapital. Solange das Konto darüber steht, ist der Rückgang null.
    /// Das ist die Regel einer festen Verlustschwelle: 100.000 Start, Schluss bei 90.000.
    /// </summary>
    InitialBalance = 1,
}
