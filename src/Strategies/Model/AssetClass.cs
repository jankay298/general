namespace Daytrading.Strategies.Model;

/// <summary>
/// Anlageklasse eines Symbols. Bestimmt später Handelszeitfenster, Sessiondefinition
/// und Kostenmodell - beides liegt in der Konfiguration, nicht in der Strategie.
/// Derivate und Optionen sind bewusst nicht abgedeckt.
/// </summary>
public enum AssetClass
{
    /// <summary>Einzelaktie. Feste Börsenzeiten, Overnight-Gaps, Splits/Dividenden.</summary>
    Equity = 0,

    /// <summary>Index bzw. Index-CFD. Feste Handelszeiten, aber meist längere Sessions als Aktien.</summary>
    Index = 1,

    /// <summary>Rohstoff einschließlich Edelmetalle (Gold, Silber). Nahezu durchgehend, mit dünnen Phasen.</summary>
    Commodity = 2,

    /// <summary>Kryptowährung. 24/7, kein natürliches Sessionende - der Handelstag wird künstlich definiert.</summary>
    Crypto = 3,
}
