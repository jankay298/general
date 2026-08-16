using System;

namespace Daytrading.Strategies.Model;

/// <summary>
/// Marktstruktur eines Symbols, so wie eine Strategie sie sehen darf: Name, Anlageklasse
/// und Tick-Größe.
/// </summary>
/// <remarks>
/// Bewusst NICHT enthalten sind Kontogröße, minimale Positionsgröße, Kommission und Spread.
/// Das ist Wissen der Ausführungsschicht. Eine Strategie, die ihre Positionsgröße selbst
/// berechnen könnte, würde die Risikoregeln des Frameworks umgehen.
/// </remarks>
public sealed class SymbolInfo
{
    public SymbolInfo(string name, AssetClass assetClass, decimal tickSize)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Symbolname darf nicht leer sein.", nameof(name));
        }

        if (tickSize <= 0m)
        {
            throw new ArgumentOutOfRangeException(
                nameof(tickSize), tickSize, $"Tick-Größe von '{name}' muss positiv sein.");
        }

        Name = name;
        AssetClass = assetClass;
        TickSize = tickSize;
    }

    public string Name { get; }

    public AssetClass AssetClass { get; }

    /// <summary>Kleinste Preisänderung des Instruments.</summary>
    public decimal TickSize { get; }

    /// <summary>Auf den nächsten Tick gerundet.</summary>
    public decimal RoundToTick(decimal price) => Math.Round(price / TickSize, MidpointRounding.AwayFromZero) * TickSize;

    /// <summary>Auf den nächsten Tick abgerundet.</summary>
    public decimal RoundDownToTick(decimal price) => Math.Floor(price / TickSize) * TickSize;

    /// <summary>Auf den nächsten Tick aufgerundet.</summary>
    public decimal RoundUpToTick(decimal price) => Math.Ceiling(price / TickSize) * TickSize;

    public override string ToString() => $"{Name} ({AssetClass}, Tick {TickSize})";
}
