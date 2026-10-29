using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Stockhog.Application.Trading;
using Stockhog.Domain.Trading;
using Microsoft.Extensions.Options;

namespace Stockhog.Infrastructure.Alpaca.Trading;

/// <summary>
/// Typed client for the Alpaca trading API (https://docs.alpaca.markets/reference/getaccount-1). Every failure is
/// reported as a <see cref="BrokerException"/>: a rejection when Alpaca answered with a client error, an unknown
/// outcome for timeouts, connection failures and server errors.
/// </summary>
internal sealed class AlpacaBrokerClient(HttpClient httpClient, IOptions<AlpacaOptions> options) : IBrokerGateway
{
    public async Task<BrokerAccount> GetAccountAsync(CancellationToken cancellationToken = default)
    {
        var account = await SendAsync<AccountDto>(HttpMethod.Get, "v2/account", null, cancellationToken).ConfigureAwait(false);
        return new BrokerAccount(
            account!.AccountNumber,
            account.Currency ?? "USD",
            Number(account.Equity),
            Number(account.LastEquity),
            Number(account.Cash),
            Number(account.BuyingPower),
            account.TradingBlocked || account.AccountBlocked,
            IsPaper: httpClient.BaseAddress?.Host.Equals(AlpacaOptions.PaperTradingHost, StringComparison.OrdinalIgnoreCase) == true);
    }

    public async Task<IReadOnlyList<BrokerPosition>> GetPositionsAsync(CancellationToken cancellationToken = default)
    {
        var positions = await SendAsync<List<PositionDto>>(HttpMethod.Get, "v2/positions", null, cancellationToken).ConfigureAwait(false);
        return (positions ?? [])
            .Select(p => new BrokerPosition(
                p.Symbol,
                Number(p.Quantity),
                Number(p.AverageEntryPrice),
                Number(p.CurrentPrice),
                Number(p.MarketValue),
                Number(p.UnrealizedProfitLoss),
                Number(p.UnrealizedProfitLossPercent) * 100))
            .ToList();
    }

    public async Task<BrokerOrder?> GetOrderAsync(ClientOrderId clientOrderId, CancellationToken cancellationToken = default)
    {
        var path = $"v2/orders:by_client_order_id?client_order_id={Uri.EscapeDataString(clientOrderId.Value)}";
        var order = await SendAsync<OrderDto>(HttpMethod.Get, path, null, cancellationToken, notFoundIsNull: true).ConfigureAwait(false);
        return order is null ? null : ToBrokerOrder(order);
    }

    public async Task<BrokerOrder> SubmitOrderAsync(Order order, CancellationToken cancellationToken = default)
    {
        var request = new SubmitOrderDto(
            order.Symbol.Value,
            Format(order.Quantity),
            order.Side == OrderSide.Buy ? "buy" : "sell",
            ToAlpacaType(order.Type),
            order.TimeInForce == TimeInForce.Gtc ? "gtc" : "day",
            order.LimitPrice is { } limit ? Format(limit) : null,
            order.StopPrice is { } stop ? Format(stop) : null,
            order.Id.Value);

        var accepted = await SendAsync<OrderDto>(HttpMethod.Post, "v2/orders", request, cancellationToken).ConfigureAwait(false);
        return ToBrokerOrder(accepted!);
    }

    public Task CancelOrderAsync(string brokerOrderId, CancellationToken cancellationToken = default) =>
        SendAsync<object>(HttpMethod.Delete, $"v2/orders/{Uri.EscapeDataString(brokerOrderId)}", null, cancellationToken);

    internal static BrokerOrder ToBrokerOrder(OrderDto order) => new(
        order.Id,
        order.ClientOrderId,
        order.Symbol,
        MapStatus(order.Status),
        Number(order.Quantity),
        Number(order.FilledQuantity),
        string.IsNullOrEmpty(order.FilledAveragePrice) ? null : Number(order.FilledAveragePrice),
        order.UpdatedAt ?? order.CreatedAt ?? DateTimeOffset.UtcNow);

    /// <summary>Alpaca has more states than the app needs; everything still working maps to Submitted.</summary>
    internal static OrderStatus MapStatus(string? status) => status switch
    {
        "partially_filled" => OrderStatus.PartiallyFilled,
        "filled" => OrderStatus.Filled,
        "canceled" or "replaced" => OrderStatus.Cancelled,
        "expired" => OrderStatus.Expired,
        "rejected" or "suspended" => OrderStatus.Rejected,
        _ => OrderStatus.Submitted
    };

    private static string ToAlpacaType(OrderType type) => type switch
    {
        OrderType.Market => "market",
        OrderType.Limit => "limit",
        OrderType.Stop => "stop",
        OrderType.StopLimit => "stop_limit",
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, null)
    };

    private async Task<T?> SendAsync<T>(HttpMethod method, string path, object? body, CancellationToken cancellationToken, bool notFoundIsNull = false)
    {
        var settings = options.Value;
        if (!settings.HasTradingCredentials)
            throw new BrokerException("Alpaca trading keys are not configured (Alpaca:TradingApiKey/TradingApiSecret or Alpaca:ApiKey/ApiSecret).", isRejection: true);

        using var request = new HttpRequestMessage(method, path);
        request.Headers.Add("APCA-API-KEY-ID", settings.TradingKey);
        request.Headers.Add("APCA-API-SECRET-KEY", settings.TradingSecret);
        if (body is not null)
            request.Content = JsonContent.Create(body, options: JsonOptions);

        HttpResponseMessage response;
        try
        {
            response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TimeoutException
                                   || ex is OperationCanceledException && !cancellationToken.IsCancellationRequested
                                   || ex.GetType().Namespace?.StartsWith("Polly", StringComparison.Ordinal) == true)
        {
            // No answer (timeout, connection lost, circuit open): the request may or may not have been carried out.
            throw new BrokerException(ex.Message, isRejection: false, ex);
        }

        using (response)
        {
            if (notFoundIsNull && response.StatusCode == HttpStatusCode.NotFound)
                return default;

            if (!response.IsSuccessStatusCode)
            {
                var message = await ErrorMessageAsync(response, cancellationToken).ConfigureAwait(false);
                var status = (int)response.StatusCode;
                throw new BrokerException(message, isRejection: status is >= 400 and < 500 && status != 429);
            }

            if (response.StatusCode == HttpStatusCode.NoContent || typeof(T) == typeof(object))
                return default;

            return await response.Content.ReadFromJsonAsync<T>(JsonOptions, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Alpaca errors look like <c>{"code":40310000,"message":"insufficient buying power"}</c>.</summary>
    private static async Task<string> ErrorMessageAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (JsonSerializer.Deserialize<ErrorDto>(text, JsonOptions)?.Message is { Length: > 0 } message)
                return message;
        }
        catch (JsonException)
        {
        }

        return $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}".Trim();
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>Alpaca sends numbers as strings, e.g. <c>"qty": "10"</c>.</summary>
    internal static decimal Number(string? value) =>
        string.IsNullOrEmpty(value) ? 0 : decimal.Parse(value, NumberStyles.Float, CultureInfo.InvariantCulture);

    private static string Format(decimal value) => value.ToString("0.#########", CultureInfo.InvariantCulture);

    private sealed record ErrorDto(int Code, string? Message);

    private sealed record AccountDto(
        [property: JsonPropertyName("account_number")] string AccountNumber,
        [property: JsonPropertyName("currency")] string? Currency,
        [property: JsonPropertyName("equity")] string? Equity,
        [property: JsonPropertyName("last_equity")] string? LastEquity,
        [property: JsonPropertyName("cash")] string? Cash,
        [property: JsonPropertyName("buying_power")] string? BuyingPower,
        [property: JsonPropertyName("trading_blocked")] bool TradingBlocked,
        [property: JsonPropertyName("account_blocked")] bool AccountBlocked);

    private sealed record PositionDto(
        [property: JsonPropertyName("symbol")] string Symbol,
        [property: JsonPropertyName("qty")] string? Quantity,
        [property: JsonPropertyName("avg_entry_price")] string? AverageEntryPrice,
        [property: JsonPropertyName("current_price")] string? CurrentPrice,
        [property: JsonPropertyName("market_value")] string? MarketValue,
        [property: JsonPropertyName("unrealized_pl")] string? UnrealizedProfitLoss,
        [property: JsonPropertyName("unrealized_plpc")] string? UnrealizedProfitLossPercent);

    private sealed record SubmitOrderDto(
        [property: JsonPropertyName("symbol")] string Symbol,
        [property: JsonPropertyName("qty")] string Quantity,
        [property: JsonPropertyName("side")] string Side,
        [property: JsonPropertyName("type")] string Type,
        [property: JsonPropertyName("time_in_force")] string TimeInForce,
        [property: JsonPropertyName("limit_price")] string? LimitPrice,
        [property: JsonPropertyName("stop_price")] string? StopPrice,
        [property: JsonPropertyName("client_order_id")] string ClientOrderId);
}

internal sealed record OrderDto(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("client_order_id")] string? ClientOrderId,
    [property: JsonPropertyName("symbol")] string Symbol,
    [property: JsonPropertyName("status")] string? Status,
    [property: JsonPropertyName("qty")] string? Quantity,
    [property: JsonPropertyName("filled_qty")] string? FilledQuantity,
    [property: JsonPropertyName("filled_avg_price")] string? FilledAveragePrice,
    [property: JsonPropertyName("created_at")] DateTimeOffset? CreatedAt,
    [property: JsonPropertyName("updated_at")] DateTimeOffset? UpdatedAt);
