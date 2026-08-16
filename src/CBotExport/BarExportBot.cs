using System;
using System.Globalization;
using System.IO;
using File = System.IO.File;
using Directory = System.IO.Directory;
using Path = System.IO.Path;
using System.Text;
using cAlgo.API;

namespace Daytrading.CBot;

/// <summary>
/// Exportiert die Bars des aktuellen Charts als CSV - die Referenzdaten des eigenen Brokers.
/// </summary>
/// <remarks>
/// Der wichtigste kleine Baustein der Datenpipeline: Nur diese Daten enthalten die
/// Preisstellung und den Spread, mit denen später tatsächlich gehandelt wird. Externe Quellen
/// liefern historische Tiefe, werden aber gegen diesen Export plausibilisiert.
///
/// Bedienung: Den cBot auf dem gewünschten Symbol und Zeitrahmen starten. Er lädt so viel
/// Historie nach, wie der Broker hergibt, schreibt eine CSV und stoppt sich selbst. Das Format
/// liest <c>CsvFileDataProvider</c> ohne weitere Einstellungen.
/// </remarks>
[Robot(AccessRights = AccessRights.FullAccess, AddIndicators = false)]
public class BarExportBot : Robot
{
    [Parameter("Zielverzeichnis", DefaultValue = "", Group = "Export")]
    public string TargetDirectory { get; set; } = string.Empty;

    [Parameter("Gewünschte Anzahl Bars", DefaultValue = 200000, MinValue = 100, Group = "Export")]
    public int DesiredBars { get; set; } = 200_000;

    [Parameter("Maximale Nachladeversuche", DefaultValue = 200, MinValue = 1, Group = "Export")]
    public int MaxLoadAttempts { get; set; } = 200;

    protected override void OnStart()
    {
        var directory = string.IsNullOrWhiteSpace(TargetDirectory)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "cAlgo-Export")
            : TargetDirectory;

        Directory.CreateDirectory(directory);

        var loaded = LoadHistory();
        var path = Path.Combine(directory, $"{SymbolName}-{TimeFrame}.csv".Replace('/', '-'));
        var text = new StringBuilder(Bars.Count * 64);

        // Kopfzeile mit Herkunft: Ohne sie ist später nicht mehr feststellbar, von welchem Broker
        // und wann die Datei stammt - und damit wäre sie als Referenz wertlos.
        text.AppendLine(string.Format(
            CultureInfo.InvariantCulture,
            "# broker={0}; konto={1}; symbol={2}; timeframe={3}; exportiert={4:yyyy-MM-dd HH:mm}Z; spread_aktuell={5}",
            Account.BrokerName, Account.Number, SymbolName, TimeFrame, Server.TimeInUtc, Symbol.Spread));
        text.AppendLine("time_utc,open,high,low,close,volume");

        for (var i = 0; i < Bars.Count; i++)
        {
            var bar = Bars[i];
            text.AppendLine(string.Format(
                CultureInfo.InvariantCulture,
                "{0:yyyy-MM-dd'T'HH:mm:ss'Z'},{1},{2},{3},{4},{5}",
                DateTime.SpecifyKind(bar.OpenTime, DateTimeKind.Utc),
                Round(bar.Open), Round(bar.High), Round(bar.Low), Round(bar.Close), bar.TickVolume));
        }

        File.WriteAllText(path, text.ToString());

        Print($"{Bars.Count} Bars nach {path} geschrieben ({loaded} Nachladevorgänge).");
        Print($"Erste Bar: {Bars[0].OpenTime:yyyy-MM-dd HH:mm} UTC, letzte: {Bars.LastBar.OpenTime:yyyy-MM-dd HH:mm} UTC.");

        if (Bars.Count < DesiredBars)
        {
            Print(
                $"HINWEIS: Der Broker hält nur {Bars.Count} Bars vor, gewünscht waren {DesiredBars}. " +
                "Für mehr Tiefe eine externe Quelle nutzen und gegen diesen Export plausibilisieren.");
        }

        Stop();
    }

    private int LoadHistory()
    {
        var attempts = 0;

        while (Bars.Count < DesiredBars && attempts < MaxLoadAttempts)
        {
            var loaded = Bars.LoadMoreHistory();
            attempts++;

            if (loaded <= 0)
            {
                // Der Broker gibt nichts mehr her - kein Fehler, nur die Grenze seiner Historie.
                break;
            }
        }

        return attempts;
    }

    private string Round(double value) => value.ToString("0.##########", CultureInfo.InvariantCulture);
}
