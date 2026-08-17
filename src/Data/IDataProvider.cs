using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Daytrading.Strategies.Model;

namespace Daytrading.Data;

/// <summary>Anfrage an eine Datenquelle. Zeitraum immer in UTC, Ende exklusiv.</summary>
public sealed class DataRequest
{
    public DataRequest(string symbol, Timeframe timeframe, DateTime fromUtc, DateTime toUtc)
    {
        if (string.IsNullOrWhiteSpace(symbol))
        {
            throw new ArgumentException("Symbol darf nicht leer sein.", nameof(symbol));
        }

        if (fromUtc.Kind != DateTimeKind.Utc || toUtc.Kind != DateTimeKind.Utc)
        {
            throw new ArgumentException("Zeitraum muss in UTC angegeben werden.", nameof(fromUtc));
        }

        if (toUtc <= fromUtc)
        {
            throw new ArgumentException($"Ende {toUtc:O} liegt nicht nach dem Beginn {fromUtc:O}.", nameof(toUtc));
        }

        Symbol = symbol;
        Timeframe = timeframe;
        FromUtc = fromUtc;
        ToUtc = toUtc;
    }

    /// <summary>Symbolname bei der Quelle - kann vom internen Namen abweichen (z.B. BTCUSDT vs. BTC/USD).</summary>
    public string Symbol { get; }

    public Timeframe Timeframe { get; }

    public DateTime FromUtc { get; }

    /// <summary>Ende des Zeitraums, exklusiv.</summary>
    public DateTime ToUtc { get; }

    public override string ToString() => $"{Symbol} {Timeframe} {FromUtc:yyyy-MM-dd} .. {ToUtc:yyyy-MM-dd}";
}

/// <summary>
/// Austauschbare Marktdatenquelle.
/// </summary>
/// <remarks>
/// Jede Quelle liefert bereits normalisierte <see cref="Candle"/>-Objekte in UTC. Alles, was
/// quellenspezifisch ist - Zeitzone, Spaltenformat, Paginierung, Zeitstempel in Sekunden,
/// Millisekunden oder Mikrosekunden - bleibt in der Implementierung und tritt nie nach außen.
///
/// Referenz für die Kostenannahmen bleiben die Daten des eigenen Brokers. Externe Quellen
/// liefern die historische Tiefe und werden gegen die Brokerdaten plausibilisiert.
/// </remarks>
public interface IDataProvider
{
    /// <summary>Name der Quelle, wie er im Manifest und im Qualitätsreport erscheint.</summary>
    string Name { get; }

    /// <summary>Feinste Auflösung, die diese Quelle liefert. Gröberes entsteht durch Aggregation.</summary>
    Timeframe NativeTimeframe { get; }

    Task<IReadOnlyList<Candle>> GetBarsAsync(DataRequest request, CancellationToken cancellationToken = default);
}

/// <summary>Fehler beim Beschaffen oder Lesen von Marktdaten.</summary>
public class MarketDataException : Exception
{
    public MarketDataException(string message)
        : base(message)
    {
    }

    public MarketDataException(string message, Exception inner)
        : base(message, inner)
    {
    }
}

/// <summary>
/// Die Daten waren nicht erreichbar - Zeitüberschreitung, Verbindungsabbruch, Drosselung.
/// </summary>
/// <remarks>
/// Eigener Typ, weil daraus eine andere Entscheidung folgt als aus einem Formatfehler: Eine
/// Störung auf der Leitung darf einen mehrjährigen Abruf nicht beenden, ein unerwartetes
/// Dateiformat dagegen muss ihn beenden - dann stimmt eine Annahme über die Quelle nicht mehr,
/// und alles Weitere wäre geraten.
/// </remarks>
public sealed class MarketDataUnavailableException : MarketDataException
{
    public MarketDataUnavailableException(string message)
        : base(message)
    {
    }

    public MarketDataUnavailableException(string message, Exception inner)
        : base(message, inner)
    {
    }
}
