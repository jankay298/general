using cAlgo.API;

namespace Daytrading.CBot;

/// <summary>
/// Bot, dessen Strategie ueber einen Parameter gewaehlt wird.
/// </summary>
/// <remarks>
/// Praktisch zum Ausprobieren, weil ein einziges Paket alle Strategien erreicht. Fuer den
/// Dauerbetrieb sind die einzelnen Bots vorzuziehen: Dort steht die Strategie im Namen des
/// Bots statt in einem Feld, das man beim Einrichten uebersieht.
/// </remarks>
[Robot(AccessRights = AccessRights.FullAccess, AddIndicators = false)]
public class DaytradingBot : DaytradingBotBase
{
    [Parameter("Strategie", DefaultValue = "OpeningRangeBreakout", Group = "Strategie")]
    public string Strategy { get; set; } = "OpeningRangeBreakout";

    protected override string StrategyName => Strategy;
}
