using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Stockhog.Application.Trading;
using Stockhog.Domain.MarketData;
using Microsoft.Extensions.Options;

namespace Stockhog.Infrastructure.Alpaca.MarketData;

/// <summary>The latest trade from the market data API (https://docs.alpaca.markets/reference/stocklatesttradesingle-1).</summary>
internal sealed class AlpacaLatestPriceClient(HttpClient httpClient, IOptions<AlpacaOptions> options) : ILatestPriceSource
{
    public async Task<decimal?> GetLatestPriceAsync(Symbol symbol, CancellationToken cancellationToken = default)
    {
        var settings = options.Value;
        if (!settings.HasCredentials)
            return null;

        using var request = new HttpRequestMessage(HttpMethod.Get,
            $"v2/stocks/{Uri.EscapeDataString(symbol.Value)}/trades/latest?feed={Uri.EscapeDataString(settings.Feed)}");
        request.Headers.Add("APCA-API-KEY-ID", settings.ApiKey);
        request.Headers.Add("APCA-API-SECRET-KEY", settings.ApiSecret);

        try
        {
            using var response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.UnprocessableEntity)
                return null;

            response.EnsureSuccessStatusCode();
            var latest = await response.Content.ReadFromJsonAsync<LatestTrade>(cancellationToken).ConfigureAwait(false);
            return latest?.Trade?.Price is > 0 and var price ? price : null;
        }
        catch (HttpRequestException ex)
        {
            throw new BrokerException($"Latest price of {symbol} unavailable: {ex.Message}", isRejection: false, ex);
        }
    }

    private sealed record LatestTrade([property: JsonPropertyName("trade")] TradeDto? Trade);

    private sealed record TradeDto([property: JsonPropertyName("p")] decimal Price);
}
