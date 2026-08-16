namespace Daytrading.Strategies.Model;

/// <summary>Richtung einer Position bzw. eines Einstiegssignals.</summary>
public enum TradeDirection
{
    Long = 0,
    Short = 1,
}

/// <summary>Hilfsfunktionen zur Handelsrichtung.</summary>
public static class TradeDirectionExtensions
{
    /// <summary>+1 für Long, -1 für Short. Erspart Fallunterscheidungen in Preisrechnungen.</summary>
    public static int Sign(this TradeDirection direction) => direction == TradeDirection.Long ? 1 : -1;

    /// <summary>Gegenrichtung.</summary>
    public static TradeDirection Opposite(this TradeDirection direction) =>
        direction == TradeDirection.Long ? TradeDirection.Short : TradeDirection.Long;
}
