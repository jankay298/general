using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Daytrading.Strategies.Model;

namespace Daytrading.Data.Providers;

/// <summary>Aus Bid- und Ask-Kerzen abgeleitete Spread-Statistik.</summary>
public sealed class SpreadStatistics
{
    public SpreadStatistics(int sampleCount, decimal median, decimal average, decimal maximum)
    {
        SampleCount = sampleCount;
        Median = median;
        Average = average;
        Maximum = maximum;
    }

    public int SampleCount { get; }

    public decimal Median { get; }

    public decimal Average { get; }

    public decimal Maximum { get; }

    public override string ToString() =>
        string.Format(
            CultureInfo.InvariantCulture,
            "Spread aus {0} Bars: Median {1}, Mittel {2}, Maximum {3}", SampleCount, Median, Average, Maximum);
}

/// <summary>
/// Metalle, Rohstoffe und Indizes über die OANDA fxTrade v20 API.
/// </summary>
/// <remarks>
/// Vorzug dieser Quelle: Sie liefert Bid-, Mid- und Ask-Kerzen getrennt. Damit lässt sich der
/// <b>historische Spread messen</b>, statt ihn zu schätzen - genau die Größe, die im Backtest
/// über Gewinn und Verlust entscheidet. Die gemessene Statistik landet im Manifest.
///
/// Grenzen der Quelle: keine Einzelaktien, maximal 5000 Kerzen je Anfrage (deshalb Paginierung)
/// und die Kurse eines anderen Brokers. Für Strategieforschung gut, für die Kostenannahmen des
/// Livebetriebs bleiben die Daten des eigenen cTrader-Kontos maßgeblich.
///
/// Zugang: Demo-Token von OANDA, per Konstruktor oder Umgebungsvariable OANDA_API_TOKEN.
/// Der Token gehört nicht ins Repository.
/// </remarks>
public sealed class OandaDataProvider : IDataProvider, IDisposable
{
    private const int MaxCandlesPerRequest = 5000;

    private readonly HttpClient _client;
    private readonly bool _ownsClient;
    private readonly Timeframe _nativeTimeframe;

    public OandaDataProvider(
        string? apiToken = null,
        bool practice = true,
        Timeframe? nativeTimeframe = null,
        HttpClient? client = null)
    {
        var token = apiToken ?? Environment.GetEnvironmentVariable("OANDA_API_TOKEN");
        if (string.IsNullOrWhiteSpace(token))
        {
            throw new MarketDataException(
                "Für OANDA wird ein API-Token gebraucht. Entweder im Konstruktor übergeben oder in der " +
                "Umgebungsvariable OANDA_API_TOKEN hinterlegen - niemals im Repository.");
        }

        _client = client ?? new HttpClient();
        _ownsClient = client == null;
        _client.BaseAddress ??= new Uri(practice
            ? "https://api-fxpractice.oanda.com/"
            : "https://api-fxtrade.oanda.com/");
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        _nativeTimeframe = nativeTimeframe ?? Timeframe.M1;
    }

    public string Name => "oanda-v20";

    public Timeframe NativeTimeframe => _nativeTimeframe;

    /// <summary>Spread-Statistik der letzten Abfrage, aus Bid- und Ask-Kerzen berechnet.</summary>
    public SpreadStatistics? LastSpreadStatistics { get; private set; }

    public async Task<IReadOnlyList<Candle>> GetBarsAsync(DataRequest request, CancellationToken cancellationToken = default)
    {
        if (request == null)
        {
            throw new ArgumentNullException(nameof(request));
        }

        var granularity = ToGranularity(request.Timeframe);
        var bars = new List<Candle>();
        var spreads = new List<decimal>();
        var cursor = request.FromUtc;

        while (cursor < request.ToUtc)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var url = string.Format(
                CultureInfo.InvariantCulture,
                "v3/instruments/{0}/candles?price=BMA&granularity={1}&from={2}&count={3}",
                Uri.EscapeDataString(request.Symbol),
                granularity,
                Uri.EscapeDataString(cursor.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture)),
                MaxCandlesPerRequest);

            using var response = await _client.GetAsync(url, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                throw new MarketDataException(
                    $"OANDA antwortete mit {(int)response.StatusCode} für {request.Symbol}: {body}");
            }

            var payload = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var lastTime = ReadCandles(payload, request, bars, spreads);

            if (lastTime == null)
            {
                break;
            }

            var next = lastTime.Value + request.Timeframe.Duration;
            if (next <= cursor)
            {
                break;   // Sicherung gegen Endlosschleifen bei unerwarteten Antworten
            }

            cursor = next;
        }

        LastSpreadStatistics = Summarize(spreads);
        return bars;
    }

    private static DateTime? ReadCandles(string payload, DataRequest request, List<Candle> bars, List<decimal> spreads)
    {
        using var document = JsonDocument.Parse(payload);
        if (!document.RootElement.TryGetProperty("candles", out var candles))
        {
            return null;
        }

        DateTime? last = null;

        foreach (var candle in candles.EnumerateArray())
        {
            if (candle.TryGetProperty("complete", out var complete) && !complete.GetBoolean())
            {
                // Unvollständige Kerzen sind noch in Bewegung - im Backtest wären sie Look-ahead.
                continue;
            }

            var time = DateTime.Parse(
                candle.GetProperty("time").GetString()!,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);
            time = DateTime.SpecifyKind(time, DateTimeKind.Utc);
            last = time;

            if (time < request.FromUtc || time >= request.ToUtc)
            {
                continue;
            }

            var volume = candle.TryGetProperty("volume", out var volumeElement) ? volumeElement.GetInt64() : 0L;
            var mid = ReadOhlc(candle, "mid");

            if (mid == null)
            {
                continue;
            }

            bars.Add(new Candle(time, mid.Value.Open, mid.Value.High, mid.Value.Low, mid.Value.Close, volume));

            var bid = ReadOhlc(candle, "bid");
            var ask = ReadOhlc(candle, "ask");
            if (bid != null && ask != null)
            {
                spreads.Add(ask.Value.Close - bid.Value.Close);
            }
        }

        return last;
    }

    private static (decimal Open, decimal High, decimal Low, decimal Close)? ReadOhlc(JsonElement candle, string name)
    {
        if (!candle.TryGetProperty(name, out var element))
        {
            return null;
        }

        return (
            decimal.Parse(element.GetProperty("o").GetString()!, CultureInfo.InvariantCulture),
            decimal.Parse(element.GetProperty("h").GetString()!, CultureInfo.InvariantCulture),
            decimal.Parse(element.GetProperty("l").GetString()!, CultureInfo.InvariantCulture),
            decimal.Parse(element.GetProperty("c").GetString()!, CultureInfo.InvariantCulture));
    }

    internal static SpreadStatistics? Summarize(List<decimal> spreads)
    {
        if (spreads.Count == 0)
        {
            return null;
        }

        spreads.Sort();
        var sum = 0m;
        var max = 0m;
        foreach (var spread in spreads)
        {
            sum += spread;
            if (spread > max)
            {
                max = spread;
            }
        }

        return new SpreadStatistics(spreads.Count, spreads[spreads.Count / 2], sum / spreads.Count, max);
    }

    private static string ToGranularity(Timeframe timeframe)
    {
        var minutes = timeframe.Duration.TotalMinutes;
        return minutes switch
        {
            1 => "M1",
            2 => "M2",
            4 => "M4",
            5 => "M5",
            10 => "M10",
            15 => "M15",
            30 => "M30",
            60 => "H1",
            120 => "H2",
            240 => "H4",
            360 => "H6",
            480 => "H8",
            720 => "H12",
            1440 => "D",
            _ => throw new MarketDataException(
                $"OANDA kennt keine Granularität {timeframe}. Feiner laden und im Code aggregieren."),
        };
    }

    public void Dispose()
    {
        if (_ownsClient)
        {
            _client.Dispose();
        }
    }
}
