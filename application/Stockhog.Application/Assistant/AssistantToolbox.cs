using System.Text.Json;
using System.Text.Json.Nodes;
using Stockhog.Application.Abstractions.Messaging;
using Stockhog.Application.Abstractions.Results;
using Stockhog.Application.Backtesting.GetBacktestReport;
using Stockhog.Application.Backtesting.GetRunRanking;
using Stockhog.Application.Backtesting.Strategies;
using Stockhog.Application.MarketData.GetMarketDataCoverage;
using Stockhog.Application.Optimization.GetBestStrategyParameters;
using Stockhog.Application.Trading;
using Stockhog.Domain.MarketData;
using Stockhog.Domain.Trading;
using Microsoft.Extensions.Logging;

namespace Stockhog.Application.Assistant;

/// <summary>
/// The app functions the assistant can call. Every tool reads through the same queries the UI uses (with their
/// validation and caching); the only one that acts is <c>propose_order_ticket</c>, and it only fills in the ticket.
/// Inputs come from the model, so they are checked here and problems are returned as errors it can correct.
/// </summary>
internal sealed class AssistantToolbox(IDispatcher dispatcher, IAssistantActions actions, ILogger<AssistantToolbox> logger) : IAssistantToolbox
{
    private const int MaxRankingRows = 100;
    private const int MaxTradesShown = 15;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    public IReadOnlyList<AssistantTool> Tools { get; } =
    [
        Tool("get_run_ranking",
            "Completed backtests with their metrics (net profit %, max drawdown %, return over drawdown, Sharpe ratio, win rate %, profit factor, trades), best net profit first. Use it to compare symbols or strategies.",
            Schema(
                ("strategy", Prop("string", "Only this strategy (its name from list_strategies); omit for all.")),
                ("symbols", new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" }, ["description"] = "Only these symbols; omit for all." }),
                ("latest_only", Prop("boolean", "Only the newest run per symbol and strategy (default true).")),
                ("limit", Prop("integer", $"Most rows to return, 1-{MaxRankingRows} (default 30)."))),
            []),
        Tool("get_run_details",
            "Full engine statistics, trade statistics and the most recent trades of one completed run. Use the symbol and run_id from get_run_ranking.",
            Schema(("symbol", Prop("string", "The run's symbol.")), ("run_id", Prop("string", "The run id."))),
            ["symbol", "run_id"]),
        Tool("list_strategies",
            "The strategies the app can backtest: what each does, its parameters with defaults, allowed ranges and the range worth testing in an optimization.",
            Schema(),
            []),
        Tool("get_best_parameters",
            "The best parameters the user's own optimizations found for a strategy: one row per optimization with symbol, period, objective, score, metrics and parameter values, newest first. 'check' holds the same parameters' results on a later period the optimization did not see, when the user ran that check: trust parameters that hold up there more than the best in-sample score.",
            Schema(("strategy", Prop("string", "The strategy name from list_strategies."))),
            ["strategy"]),
        Tool("get_market_data_coverage",
            "Which symbols have market data stored locally, at which interval, and for which days.",
            Schema(),
            []),
        Tool("get_latest_prices",
            $"The latest trade price of up to {GetLatestPricesQuery.MaxSymbols} symbols.",
            Schema(("symbols", new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" }, ["description"] = "Ticker symbols, e.g. AAPL." })),
            ["symbols"]),
        Tool("get_account",
            "The trading account (paper or live, equity, cash, buying power, today's P/L) and its open positions.",
            Schema(),
            []),
        Tool("get_risk_limits",
            "The risk limits every order must pass: whether trading is enabled, largest order and position value in USD, daily loss limit, most open orders.",
            Schema(),
            []),
        Tool("propose_order_ticket",
            "Fills in the order ticket on the Paper trading page for the user to review. It does not place the order: the user checks it and submits it, and the app's risk checks and confirmation still apply. Use only when the user asks for an order or agrees to one.",
            Schema(
                ("symbol", Prop("string", "Ticker symbol.")),
                ("side", Enum("buy", "sell")),
                ("quantity", Prop("number", "Number of shares, above zero.")),
                ("order_type", Enum("market", "limit")),
                ("limit_price", Prop("number", "Limit price in USD; required for limit orders.")),
                ("reason", Prop("string", "One sentence on why, shown to the user next to the ticket."))),
            ["symbol", "side", "quantity", "order_type", "reason"])
    ];

    public async Task<AssistantToolResult> ExecuteAsync(string toolName, JsonElement input, CancellationToken cancellationToken = default)
    {
        try
        {
            return toolName switch
            {
                "get_run_ranking" => await RankingAsync(input, cancellationToken),
                "get_run_details" => await DetailsAsync(input, cancellationToken),
                "list_strategies" => Json(await dispatcher.SendAsync(new GetStrategiesQuery(), cancellationToken), StrategiesView),
                "get_best_parameters" => Json(
                    await dispatcher.SendAsync(new GetBestStrategyParametersQuery(RequiredString(input, "strategy"), Take: 20), cancellationToken),
                    best => best.Select(b => new
                    {
                        b.Symbol, Period = $"{b.Start:yyyy-MM-dd}..{b.End:yyyy-MM-dd}", Objective = b.Objective.ToString(), b.Score,
                        b.Metrics.NetProfitPercent, b.Metrics.MaxDrawdownPercent, b.Metrics.SharpeRatio, b.Metrics.TotalTrades, b.Parameters,
                        // The same parameters on days the optimization did not see, when the user ran that check.
                        Check = b.CheckMetrics is { } check
                            ? new
                            {
                                Period = $"{b.CheckStart:yyyy-MM-dd}..{b.CheckEnd:yyyy-MM-dd}", Score = b.CheckScore,
                                check.NetProfitPercent, check.MaxDrawdownPercent, check.SharpeRatio, check.TotalTrades
                            }
                            : null
                    })),
                "get_market_data_coverage" => Json(await dispatcher.SendAsync(new GetMarketDataCoverageQuery(), cancellationToken), CoverageView),
                "get_latest_prices" => Json(await dispatcher.SendAsync(new GetLatestPricesQuery(StringArray(input, "symbols")), cancellationToken), p => p),
                "get_account" => await AccountAsync(cancellationToken),
                "get_risk_limits" => Json(await dispatcher.SendAsync(new GetRiskLimitsQuery(), cancellationToken), l => l),
                "propose_order_ticket" => ProposeOrder(input),
                _ => new AssistantToolResult($"Unknown tool '{toolName}'.", IsError: true)
            };
        }
        catch (Exception ex) when (ex is ToolInputException or JsonException or InvalidOperationException or FormatException)
        {
            return new AssistantToolResult($"Invalid input: {ex.Message}", IsError: true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Assistant tool {Tool} failed", toolName);
            return new AssistantToolResult($"The tool failed: {ex.Message}", IsError: true);
        }
    }

    private async Task<AssistantToolResult> RankingAsync(JsonElement input, CancellationToken cancellationToken)
    {
        var limit = Math.Clamp(OptionalInt(input, "limit") ?? 30, 1, MaxRankingRows);
        var query = new GetRunRankingQuery(OptionalString(input, "strategy"), OptionalBool(input, "latest_only") ?? true, OptionalStringArray(input, "symbols"));
        var ranking = await dispatcher.SendAsync(query, cancellationToken);

        return Json(ranking, runs => new
        {
            TotalRuns = runs.Count,
            Shown = Math.Min(limit, runs.Count),
            Runs = runs.Take(limit).Select(r => new
            {
                r.Symbol, Strategy = r.StrategyName, r.RunId, Period = $"{r.Start:yyyy-MM-dd}..{r.End:yyyy-MM-dd}",
                r.NetProfitPercent, r.MaxDrawdownPercent, r.ReturnOverDrawdown, r.SharpeRatio, r.WinRatePercent, r.ProfitFactor, r.TotalTrades
            })
        });
    }

    private async Task<AssistantToolResult> DetailsAsync(JsonElement input, CancellationToken cancellationToken)
    {
        var report = await dispatcher.SendAsync(new GetBacktestReportQuery(RequiredString(input, "symbol"), RequiredString(input, "run_id")), cancellationToken);

        return Json(report, r => new
        {
            r.RunId,
            EngineStatistics = r.Statistics,
            r.TradeStatistics,
            StartEquity = r.Equity.Count > 0 ? r.Equity[0].Value : (decimal?)null,
            EndEquity = r.Equity.Count > 0 ? r.Equity[^1].Value : (decimal?)null,
            RecentTrades = r.ClosedTrades.TakeLast(MaxTradesShown).Select(t => new
            {
                t.Direction, t.EntryTime, t.EntryPrice, t.ExitTime, t.ExitPrice, t.ExitReason, t.ProfitLoss,
                ProfitLossPercent = Math.Round(t.ProfitLossPercent * 100, 2)
            })
        });
    }

    private async Task<AssistantToolResult> AccountAsync(CancellationToken cancellationToken)
    {
        var account = await dispatcher.SendAsync(new GetAccountQuery(), cancellationToken);
        if (account.IsFailure)
            return Error(account.Error);

        var positions = await dispatcher.SendAsync(new GetPositionsQuery(), cancellationToken);
        if (positions.IsFailure)
            return Error(positions.Error);

        var a = account.Value;
        return Serialize(new
        {
            Account = a.IsPaper ? "paper" : "LIVE",
            a.Currency, a.Equity, a.Cash, a.BuyingPower, TodayProfitLoss = a.DailyProfitLoss, a.TradingBlocked,
            Positions = positions.Value.Positions
        });
    }

    private AssistantToolResult ProposeOrder(JsonElement input)
    {
        var symbol = RequiredString(input, "symbol").Trim().ToUpperInvariant();
        if (!Symbol.IsValid(symbol))
            throw new ToolInputException($"'{symbol}' is not a valid symbol.");

        var side = RequiredString(input, "side") switch
        {
            "buy" => OrderSide.Buy,
            "sell" => OrderSide.Sell,
            var other => throw new ToolInputException($"side must be buy or sell, not '{other}'.")
        };
        var type = RequiredString(input, "order_type") switch
        {
            "market" => OrderType.Market,
            "limit" => OrderType.Limit,
            var other => throw new ToolInputException($"order_type must be market or limit, not '{other}'.")
        };

        var quantity = RequiredDecimal(input, "quantity");
        if (quantity <= 0)
            throw new ToolInputException("quantity must be above zero.");

        var limitPrice = OptionalDecimal(input, "limit_price");
        if (type == OrderType.Limit && limitPrice is not > 0)
            throw new ToolInputException("A limit order needs limit_price above zero.");

        actions.ProposeOrder(new OrderProposal(symbol, side, type, quantity, type == OrderType.Limit ? limitPrice : null, RequiredString(input, "reason")));
        return new AssistantToolResult(
            "The order ticket on the Paper trading page is filled in. Nothing was sent: the user reviews it and decides; the app's risk checks run when they submit.");
    }

    private static object StrategiesView(IReadOnlyList<StrategyDescriptor> strategies) =>
        strategies.Select(s => new
        {
            s.Name, s.DisplayName, s.Description,
            Parameters = s.Parameters.Select(p => new
            {
                p.Name, p.DisplayName, p.Description, Default = p.DefaultValue, p.Minimum, p.Maximum,
                SuggestedToTest = p.Suggested is { } r ? $"{r.From}..{r.To} step {r.Step}" : null
            })
        });

    private static object CoverageView(IReadOnlyList<Application.MarketData.MarketDataCoverage> coverage) =>
        coverage.Select(c => new { c.Symbol, Interval = c.Timeframe.ToString(), Days = c.CoveredDays, Ranges = c.RangesText });

    private static AssistantToolResult Json<T>(Result<T> result, Func<T, object?> view) =>
        result.IsFailure ? Error(result.Error) : Serialize(view(result.Value));

    private static AssistantToolResult Serialize(object? value) => new(JsonSerializer.Serialize(value, JsonOptions));

    private static AssistantToolResult Error(Error error) => new(error.Message, IsError: true);

    private static AssistantTool Tool(string name, string description, JsonObject schema, string[] required)
    {
        schema["required"] = new JsonArray(required.Select(r => (JsonNode)r).ToArray());
        return new AssistantTool(name, description, schema);
    }

    private static JsonObject Schema(params (string Name, JsonNode Schema)[] properties)
    {
        var props = new JsonObject();
        foreach (var (name, schema) in properties)
            props[name] = schema;
        return new JsonObject { ["type"] = "object", ["properties"] = props };
    }

    private static JsonObject Prop(string type, string description) => new() { ["type"] = type, ["description"] = description };

    private static JsonObject Enum(params string[] values) => new() { ["type"] = "string", ["enum"] = new JsonArray(values.Select(v => (JsonNode)v).ToArray()) };

    private static string RequiredString(JsonElement input, string name) =>
        OptionalString(input, name) ?? throw new ToolInputException($"{name} is required.");

    private static string? OptionalString(JsonElement input, string name) =>
        input.ValueKind == JsonValueKind.Object && input.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static decimal RequiredDecimal(JsonElement input, string name) =>
        OptionalDecimal(input, name) ?? throw new ToolInputException($"{name} is required and must be a number.");

    private static decimal? OptionalDecimal(JsonElement input, string name) =>
        input.ValueKind == JsonValueKind.Object && input.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetDecimal()
            : null;

    private static int? OptionalInt(JsonElement input, string name) =>
        OptionalDecimal(input, name) is { } number ? (int)Math.Round(number) : null;

    private static bool? OptionalBool(JsonElement input, string name) =>
        input.ValueKind == JsonValueKind.Object && input.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : null;

    private static IReadOnlyList<string> StringArray(JsonElement input, string name) =>
        OptionalStringArray(input, name) ?? throw new ToolInputException($"{name} is required: a list of strings.");

    private static IReadOnlyList<string>? OptionalStringArray(JsonElement input, string name) =>
        input.ValueKind == JsonValueKind.Object && input.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray().Where(v => v.ValueKind == JsonValueKind.String).Select(v => v.GetString()!).ToList()
            : null;

    private sealed class ToolInputException(string message) : Exception(message);
}
