using cAlgo.API;

namespace Daytrading.CBot;

/// <summary>Rueckkehr zum VWAP nach Abweichung.</summary>
/// <remarks>
/// Traegt keine eigene Logik: Die Regeln stehen in der Strategie <c>VwapReversion</c>, die
/// Risikogrenzen in der Ausfuehrungsschicht. Hier wird nur festgelegt, welche der beiden
/// dieser Bot handelt - damit ein Konto genau eine Strategie haelt und ihr Ergebnis
/// eindeutig zuzuordnen ist.
/// </remarks>
[Robot(AccessRights = AccessRights.FullAccess, AddIndicators = false)]
public class VwapBot : DaytradingBotBase
{
    protected override string StrategyName => "VwapReversion";
}
