using System;

namespace Daytrading.Strategies;

/// <summary>
/// Identität einer Strategie. Erscheint in der Ergebnismatrix, im Trade-Log und im
/// Erwartungsprofil der Bewertungsschicht.
/// </summary>
/// <remarks>
/// Die <see cref="Version"/> ist wichtiger, als sie aussieht: Ein eingefrorenes
/// Erwartungsprofil gilt für genau eine Version einer Strategie. Wird die Logik geändert,
/// muss die Version steigen, sonst wird Live-Verhalten gegen ein Profil gemessen,
/// das zu anderem Code gehört.
/// </remarks>
public sealed class StrategyDescriptor
{
    public StrategyDescriptor(string name, string version, string description = "")
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Strategiename darf nicht leer sein.", nameof(name));
        }

        if (string.IsNullOrWhiteSpace(version))
        {
            throw new ArgumentException("Strategieversion darf nicht leer sein.", nameof(version));
        }

        Name = name;
        Version = version;
        Description = description ?? string.Empty;
    }

    public string Name { get; }

    public string Version { get; }

    public string Description { get; }

    /// <summary>Stabiler Schlüssel für Dateinamen und Matrixzeilen.</summary>
    public string Key => $"{Name}@{Version}";

    public override string ToString() => Key;
}
