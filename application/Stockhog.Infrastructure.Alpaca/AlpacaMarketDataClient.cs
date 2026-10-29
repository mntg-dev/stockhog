using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using Stockhog.Domain.MarketData;
using Stockhog.Infrastructure.MarketData;
using Microsoft.Extensions.Options;

namespace Stockhog.Infrastructure.Alpaca;

/// <summary>
/// Typed client for the Alpaca historical stock bars API (https://docs.alpaca.markets/reference/stockbarsingle).
/// </summary>
internal sealed class AlpacaMarketDataClient(HttpClient httpClient, IOptions<AlpacaOptions> options) : IHistoricalBarSource
{
    private const int PageLimit = 10_000;

    /// <param name="coverage">Receives the fraction (0..1) of the requested range fetched so far.</param>
    public async Task<IReadOnlyList<MarketBar>> GetBarsAsync(
        string symbol,
        Timeframe timeframe,
        DateTime startUtc,
        DateTime endUtc,
        IProgress<double>? coverage,
        CancellationToken cancellationToken)
    {
        var bars = new List<MarketBar>();
        await foreach (var page in StreamPagesAsync(symbol, timeframe, startUtc, endUtc, coverage, cancellationToken).ConfigureAwait(false))
            bars.AddRange(page);

        return bars;
    }

    /// <summary>
    /// Yields each page (up to 10,000 bars, oldest first) as soon as it arrives, so the caller can process
    /// one page while the next is downloading.
    /// </summary>
    /// <param name="coverage">Receives the fraction (0..1) of the requested range fetched so far.</param>
    public async IAsyncEnumerable<IReadOnlyList<MarketBar>> StreamPagesAsync(
        string symbol,
        Timeframe timeframe,
        DateTime startUtc,
        DateTime endUtc,
        IProgress<double>? coverage,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var settings = options.Value;
        if (!settings.HasCredentials)
            throw new AlpacaConfigurationException();

        string? pageToken = null;

        do
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, BuildUrl(symbol, timeframe, startUtc, endUtc, settings.Feed, pageToken));
            request.Headers.Add("APCA-API-KEY-ID", settings.ApiKey);
            request.Headers.Add("APCA-API-SECRET-KEY", settings.ApiSecret);

            using var response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            var page = await response.Content.ReadFromJsonAsync<BarsPage>(cancellationToken).ConfigureAwait(false);
            if (page?.Bars is { Count: > 0 } pageBars)
            {
                var bars = pageBars.Select(b => new MarketBar(b.Time.ToUniversalTime(), b.Open, b.High, b.Low, b.Close, b.Volume)).ToList();
                coverage?.Report(Fraction(startUtc, endUtc, bars[^1].TimeUtc));
                yield return bars;
            }

            pageToken = page?.NextPageToken;
        }
        while (!string.IsNullOrEmpty(pageToken));

        coverage?.Report(1);
    }

    private static string BuildUrl(string symbol, Timeframe timeframe, DateTime startUtc, DateTime endUtc, string feed, string? pageToken)
    {
        var url = $"v2/stocks/{Uri.EscapeDataString(symbol)}/bars" +
                  $"?timeframe={ToAlpacaTimeframe(timeframe)}" +
                  $"&start={startUtc:yyyy-MM-ddTHH:mm:ssZ}" +
                  $"&end={endUtc:yyyy-MM-ddTHH:mm:ssZ}" +
                  $"&limit={PageLimit}" +
                  "&adjustment=raw" +
                  $"&feed={Uri.EscapeDataString(feed)}";

        return pageToken is null ? url : url + $"&page_token={Uri.EscapeDataString(pageToken)}";
    }

    internal static string ToAlpacaTimeframe(Timeframe timeframe) => timeframe switch
    {
        Timeframe.Minute => "1Min",
        Timeframe.Hour => "1Hour",
        Timeframe.Daily => "1Day",
        _ => throw new ArgumentOutOfRangeException(nameof(timeframe), timeframe, null)
    };

    private static double Fraction(DateTime start, DateTime end, DateTime current)
    {
        var total = (end - start).TotalSeconds;
        return total <= 0 ? 1 : Math.Clamp((current - start).TotalSeconds / total, 0, 1);
    }

    private sealed record BarsPage(
        [property: JsonPropertyName("bars")] List<BarDto>? Bars,
        [property: JsonPropertyName("next_page_token")] string? NextPageToken);

    private sealed record BarDto(
        [property: JsonPropertyName("t")] DateTime Time,
        [property: JsonPropertyName("o")] decimal Open,
        [property: JsonPropertyName("h")] decimal High,
        [property: JsonPropertyName("l")] decimal Low,
        [property: JsonPropertyName("c")] decimal Close,
        [property: JsonPropertyName("v")] long Volume);
}

public sealed class AlpacaConfigurationException() : InvalidOperationException(
    "Alpaca API credentials are not configured. Run: " +
    "dotnet user-secrets set \"Alpaca:ApiKey\" \"<key>\" --project Stockhog.App, " +
    "then the same for \"Alpaca:ApiSecret\".");
