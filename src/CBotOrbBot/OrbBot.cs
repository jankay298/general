using cAlgo.API;

namespace Daytrading.CBot;

/// <summary>Ausbruch aus der Eroeffnungsspanne.</summary>
/// <remarks>
/// Traegt keine eigene Logik: Die Regeln stehen in der Strategie <c>OpeningRangeBreakout</c>, die
/// Risikogrenzen in der Ausfuehrungsschicht. Hier wird nur festgelegt, welche der beiden
/// dieser Bot handelt - damit ein Konto genau eine Strategie haelt und ihr Ergebnis
/// eindeutig zuzuordnen ist.
/// </remarks>
[Robot(AccessRights = AccessRights.FullAccess, AddIndicators = false)]
public class OrbBot : DaytradingBotBase
{
    protected override string StrategyName => "OpeningRangeBreakout";
}
