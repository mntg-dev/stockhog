using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Stockhog.Application.Screening;
using Stockhog.Infrastructure.MarketData;
using Microsoft.Extensions.Options;

namespace Stockhog.Infrastructure.Alpaca.MarketData;

/// <summary>
/// Daily bars for many symbols per request (https://docs.alpaca.markets/reference/stockbars). The page limit counts
/// bars across all symbols, so a batch of 200 symbols × a year takes about five pages.
/// </summary>
internal sealed class AlpacaDailyBarsClient(HttpClient httpClient, IOptions<AlpacaOptions> options) : IDailyBarSource
{
    private const int PageLimit = 10_000;

    public bool IsConfigured => options.Value.HasCredentials;

    // Keeps the URL well under common length limits (symbols are at most 10 characters plus a comma).
    public int MaxSymbolsPerRequest => 200;

    public async Task<IReadOnlyDictionary<string, IReadOnlyList<DailyBar>>> GetDailyBarsAsync(
        IReadOnlyList<string> symbols,
        DateOnly start,
        DateOnly end,
        CancellationToken cancellationToken = default)
    {
        var settings = options.Value;
        if (!settings.HasCredentials)
            throw new AlpacaConfigurationException();
        if (symbols.Count == 0 || end < start)
            return new Dictionary<string, IReadOnlyList<DailyBar>>();

        var result = new Dictionary<string, List<DailyBar>>(StringComparer.Ordinal);
        string? pageToken = null;

        do
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, BuildUrl(symbols, start, end, settings.ScreenerFeed, pageToken));
            request.Headers.Add("APCA-API-KEY-ID", settings.ApiKey);
            request.Headers.Add("APCA-API-SECRET-KEY", settings.ApiSecret);

            using var response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.Forbidden)
                throw new HttpRequestException(
                    $"Alpaca refused daily bars from the \"{settings.ScreenerFeed}\" feed (403). If your plan does not include it, set Alpaca:ScreenerFeed to \"iex\".",
                    null,
                    response.StatusCode);
            response.EnsureSuccessStatusCode();

            var page = await response.Content.ReadFromJsonAsync<BarsPage>(cancellationToken).ConfigureAwait(false);
            foreach (var (symbol, bars) in page?.Bars ?? [])
            {
                if (!result.TryGetValue(symbol, out var list))
                    result[symbol] = list = [];
                list.AddRange(bars.Select(b => new DailyBar(DateOnly.FromDateTime(ExchangeTime.FromUtc(b.Time.ToUniversalTime())), b.Open, b.High, b.Low, b.Close, b.Volume)));
            }

            pageToken = page?.NextPageToken;
        }
        while (!string.IsNullOrEmpty(pageToken));

        return result.ToDictionary(p => p.Key, p => (IReadOnlyList<DailyBar>)p.Value, StringComparer.Ordinal);
    }

    /// <remarks>
    /// Daily bars are stamped at midnight New York time (04:00 or 05:00 UTC), so midnight UTC after the last day
    /// includes it and excludes the next one. That moment is always hours in the past, as SIP on the free plan needs.
    /// </remarks>
    private static string BuildUrl(IReadOnlyList<string> symbols, DateOnly start, DateOnly end, string feed, string? pageToken)
    {
        var url = "v2/stocks/bars" +
                  $"?symbols={Uri.EscapeDataString(string.Join(',', symbols))}" +
                  "&timeframe=1Day" +
                  $"&start={start:yyyy-MM-dd}T00:00:00Z" +
                  $"&end={end.AddDays(1):yyyy-MM-dd}T00:00:00Z" +
                  $"&limit={PageLimit}" +
                  "&adjustment=split" +
                  "&sort=asc" +
                  $"&feed={Uri.EscapeDataString(feed)}";

        return pageToken is null ? url : url + $"&page_token={Uri.EscapeDataString(pageToken)}";
    }

    private sealed record BarsPage(
        [property: JsonPropertyName("bars")] Dictionary<string, List<BarDto>>? Bars,
        [property: JsonPropertyName("next_page_token")] string? NextPageToken);

    private sealed record BarDto(
        [property: JsonPropertyName("t")] DateTime Time,
        [property: JsonPropertyName("o")] decimal Open,
        [property: JsonPropertyName("h")] decimal High,
        [property: JsonPropertyName("l")] decimal Low,
        [property: JsonPropertyName("c")] decimal Close,
        [property: JsonPropertyName("v")] long Volume);
}
