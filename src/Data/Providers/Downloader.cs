using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace Daytrading.Data.Providers;

/// <summary>
/// Holt eine Datei über HTTP. Abstrahiert, damit die Provider ohne Netz testbar sind.
/// </summary>
public interface IFileDownloader
{
    /// <summary>Lädt eine Datei. Gibt null zurück, wenn sie nicht existiert (HTTP 404).</summary>
    Task<byte[]?> TryDownloadAsync(string url, CancellationToken cancellationToken = default);
}

/// <summary>Download über HTTP mit Wiederholungen bei Netzfehlern.</summary>
public sealed class HttpFileDownloader : IFileDownloader, IDisposable
{
    private readonly HttpClient _client;
    private readonly bool _ownsClient;
    private readonly int _maxAttempts;

    public HttpFileDownloader(HttpClient? client = null, int maxAttempts = 4)
    {
        _client = client ?? new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        _ownsClient = client == null;
        _maxAttempts = Math.Max(1, maxAttempts);
    }

    public async Task<byte[]?> TryDownloadAsync(string url, CancellationToken cancellationToken = default)
    {
        Exception? last = null;

        for (var attempt = 1; attempt <= _maxAttempts; attempt++)
        {
            try
            {
                using var response = await _client.GetAsync(url, cancellationToken).ConfigureAwait(false);

                if (response.StatusCode == HttpStatusCode.NotFound)
                {
                    // Kein Fehler: Monatsarchive gibt es erst, wenn der Monat vorbei ist.
                    return null;
                }

                response.EnsureSuccessStatusCode();
                return await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception error) when (error is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
            {
                last = error;
                if (attempt < _maxAttempts)
                {
                    // 2s, 4s, 8s - Netzprobleme sind meist vorübergehend, ein Abbruch mitten in
                    // einem mehrjährigen Download wäre teuer.
                    await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, attempt)), cancellationToken).ConfigureAwait(false);
                }
            }
        }

        throw new MarketDataException($"Download von '{url}' ist nach {_maxAttempts} Versuchen fehlgeschlagen.", last!);
    }

    public void Dispose()
    {
        if (_ownsClient)
        {
            _client.Dispose();
        }
    }
}
