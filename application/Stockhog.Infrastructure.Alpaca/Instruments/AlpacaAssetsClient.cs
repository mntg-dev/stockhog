using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Stockhog.Application.Instruments;
using Microsoft.Extensions.Options;

namespace Stockhog.Infrastructure.Alpaca.Instruments;

/// <summary>
/// Active US equities and ETFs from the trading API (https://docs.alpaca.markets/reference/get-v2-assets-1).
/// One request returns the whole list, about 11k assets.
/// </summary>
internal sealed class AlpacaAssetsClient(HttpClient httpClient, IOptions<AlpacaOptions> options) : IInstrumentSource
{
    public bool IsConfigured => options.Value.HasTradingCredentials;

    public async Task<IReadOnlyList<InstrumentInfo>> GetTradableInstrumentsAsync(CancellationToken cancellationToken = default)
    {
        var settings = options.Value;
        if (!settings.HasTradingCredentials)
            throw new AlpacaConfigurationException();

        using var request = new HttpRequestMessage(HttpMethod.Get, "v2/assets?status=active&asset_class=us_equity");
        request.Headers.Add("APCA-API-KEY-ID", settings.TradingKey);
        request.Headers.Add("APCA-API-SECRET-KEY", settings.TradingSecret);

        using var response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var assets = await response.Content.ReadFromJsonAsync<List<AssetDto>>(cancellationToken).ConfigureAwait(false) ?? [];
        return assets
            .Where(a => a.Tradable && !string.IsNullOrWhiteSpace(a.Symbol))
            .Select(a => new InstrumentInfo(a.Symbol!, a.Name ?? string.Empty, a.Exchange ?? string.Empty))
            .ToList();
    }

    private sealed record AssetDto(
        [property: JsonPropertyName("symbol")] string? Symbol,
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("exchange")] string? Exchange,
        [property: JsonPropertyName("tradable")] bool Tradable);
}
