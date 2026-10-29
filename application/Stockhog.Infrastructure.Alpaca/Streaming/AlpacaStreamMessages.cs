using System.Text.Json;
using Stockhog.Application.Trading;
using Stockhog.Infrastructure.Alpaca.Trading;

namespace Stockhog.Infrastructure.Alpaca.Streaming;

/// <summary>One parsed item of the market data stream.</summary>
internal abstract record MarketEvent(string Symbol, DateTimeOffset Time);

internal sealed record TradeEvent(string Symbol, decimal Price, DateTimeOffset Time) : MarketEvent(Symbol, Time);

internal sealed record QuoteEvent(string Symbol, decimal Bid, decimal Ask, DateTimeOffset Time) : MarketEvent(Symbol, Time);

internal sealed record BarEvent(string Symbol, LiveBar Bar) : MarketEvent(Symbol, new DateTimeOffset(Bar.TimeUtc, TimeSpan.Zero));

/// <summary>
/// Parses Alpaca stream messages (https://docs.alpaca.markets/docs/real-time-stock-pricing-data and
/// https://docs.alpaca.markets/docs/websocket-streaming). Pure functions, so the protocol is unit-tested.
/// </summary>
internal static class AlpacaStreamMessages
{
    /// <summary>
    /// Market data messages are arrays: <c>[{"T":"t","S":"AAPL","p":187.1,"t":"..."},{"T":"q",...}]</c>.
    /// Control messages (<c>success</c>, <c>subscription</c>) are skipped; <c>error</c> messages are returned separately.
    /// </summary>
    public static (IReadOnlyList<MarketEvent> Events, IReadOnlyList<string> Errors) ParseMarketData(string json)
    {
        var events = new List<MarketEvent>();
        var errors = new List<string>();
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
            return (events, errors);

        foreach (var item in document.RootElement.EnumerateArray())
        {
            var symbol = item.TryGetProperty("S", out var s) ? s.GetString() : null;
            switch (item.TryGetProperty("T", out var type) ? type.GetString() : null)
            {
                case "t" when symbol is not null:
                    events.Add(new TradeEvent(symbol, item.GetProperty("p").GetDecimal(), Time(item)));
                    break;
                case "q" when symbol is not null:
                    events.Add(new QuoteEvent(symbol, item.GetProperty("bp").GetDecimal(), item.GetProperty("ap").GetDecimal(), Time(item)));
                    break;
                case "b" or "u" when symbol is not null:
                    events.Add(new BarEvent(symbol, new LiveBar(
                        Time(item).UtcDateTime,
                        item.GetProperty("o").GetDecimal(),
                        item.GetProperty("h").GetDecimal(),
                        item.GetProperty("l").GetDecimal(),
                        item.GetProperty("c").GetDecimal(),
                        item.TryGetProperty("v", out var v) ? v.GetInt64() : 0)));
                    break;
                case "error":
                    errors.Add($"{(item.TryGetProperty("code", out var code) ? code.GetInt32() : 0)}: {item.GetProperty("msg").GetString()}");
                    break;
            }
        }

        return (events, errors);
    }

    /// <summary>Market data control message types in a response, e.g. <c>success: authenticated</c>.</summary>
    public static IReadOnlyList<(string Type, string? Message)> ParseControl(string json)
    {
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
            return [];

        return document.RootElement.EnumerateArray()
            .Select(item => (
                item.TryGetProperty("T", out var t) ? t.GetString() ?? string.Empty : string.Empty,
                item.TryGetProperty("msg", out var m) ? m.GetString() : null))
            .ToList();
    }

    /// <summary>
    /// Trading stream messages are objects: <c>{"stream":"trade_updates","data":{"event":"fill","timestamp":"...","order":{...}}}</c>.
    /// </summary>
    /// <returns>The stream name and its data element as raw JSON.</returns>
    public static (string Stream, JsonElement Data) ParseTradingEnvelope(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var stream = root.TryGetProperty("stream", out var s) ? s.GetString() ?? string.Empty : string.Empty;
        var data = root.TryGetProperty("data", out var d) ? d.Clone() : default;
        return (stream, data);
    }

    public static TradeUpdate? ParseTradeUpdate(JsonElement data)
    {
        if (data.ValueKind != JsonValueKind.Object || !data.TryGetProperty("order", out var orderElement))
            return null;

        var order = orderElement.Deserialize<OrderDto>(StreamJson.Options);
        if (order is null)
            return null;

        var name = data.TryGetProperty("event", out var e) ? e.GetString() ?? string.Empty : string.Empty;
        var at = data.TryGetProperty("timestamp", out var ts) && ts.TryGetDateTimeOffset(out var time) ? time : order.UpdatedAt ?? DateTimeOffset.UtcNow;
        var reported = AlpacaBrokerClient.ToBrokerOrder(order);

        // A rejection's reason comes with the event, not the order.
        if (name == "rejected" && data.TryGetProperty("reason", out var reason))
            reported = reported with { Reason = reason.GetString() };

        return new TradeUpdate(name, reported, at);
    }

    private static DateTimeOffset Time(JsonElement item) =>
        item.TryGetProperty("t", out var t) && t.TryGetDateTimeOffset(out var time) ? time : DateTimeOffset.UtcNow;
}
