using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Stockhog.Application.Common;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Stockhog.Infrastructure.Alpaca.Trading;

/// <summary>
/// The exchange calendar from the trading API (<c>/v2/calendar</c>), with holidays and early closes. A month around
/// today is read once and kept while the dates asked for fall inside it; without keys or an answer the caller falls
/// back to weekdays.
/// </summary>
internal sealed class AlpacaMarketCalendar(
    HttpClient http,
    IOptions<AlpacaOptions> options,
    MarketCalendarCache cache,
    TimeProvider clock,
    ILogger<AlpacaMarketCalendar> logger) : IMarketCalendar
{
    public async Task<IReadOnlyList<MarketSession>?> GetSessionsAsync(DateOnly from, DateOnly to, CancellationToken cancellationToken = default)
    {
        var settings = options.Value;
        if (!settings.HasTradingCredentials)
            return null;

        await cache.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (cache.Sessions is not { } cached || from < cached.From || to > cached.To)
            {
                var today = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);
                var start = from < today.AddDays(-30) ? from : today.AddDays(-30);
                var end = to > today.AddDays(30) ? to : today.AddDays(30);
                if (await ReadAsync(settings, start, end, cancellationToken).ConfigureAwait(false) is not { } sessions)
                    return null;
                cache.Sessions = cached = (start, end, sessions);
            }

            return cached.Sessions.Where(s => s.Date >= from && s.Date <= to).ToList();
        }
        finally
        {
            cache.Gate.Release();
        }
    }

    private async Task<IReadOnlyList<MarketSession>?> ReadAsync(AlpacaOptions settings, DateOnly from, DateOnly to, CancellationToken cancellationToken)
    {
        var url = $"{settings.TradingBaseUrl}/v2/calendar?start={from:yyyy-MM-dd}&end={to:yyyy-MM-dd}";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("APCA-API-KEY-ID", settings.TradingKey);
        request.Headers.Add("APCA-API-SECRET-KEY", settings.TradingSecret);

        try
        {
            using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("Market calendar not available: HTTP {Status}", (int)response.StatusCode);
                return null;
            }

            var days = await response.Content.ReadFromJsonAsync<List<CalendarDto>>(cancellationToken).ConfigureAwait(false) ?? [];
            return days.Select(d => new MarketSession(
                    DateOnly.ParseExact(d.Date, "yyyy-MM-dd", CultureInfo.InvariantCulture),
                    TimeOnly.ParseExact(d.Open, "HH:mm", CultureInfo.InvariantCulture),
                    TimeOnly.ParseExact(d.Close, "HH:mm", CultureInfo.InvariantCulture)))
                .ToList();
        }
        catch (Exception ex) when (ex is HttpRequestException or System.Text.Json.JsonException or FormatException
                                   || ex is TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("Market calendar not available: {Message}", ex.Message);
            return null;
        }
    }

    private sealed record CalendarDto(
        [property: JsonPropertyName("date")] string Date,
        [property: JsonPropertyName("open")] string Open,
        [property: JsonPropertyName("close")] string Close);
}

/// <summary>The sessions read last, shared by the (short-lived) calendar clients.</summary>
internal sealed class MarketCalendarCache
{
    public SemaphoreSlim Gate { get; } = new(1, 1);

    public (DateOnly From, DateOnly To, IReadOnlyList<MarketSession> Sessions)? Sessions { get; set; }
}
