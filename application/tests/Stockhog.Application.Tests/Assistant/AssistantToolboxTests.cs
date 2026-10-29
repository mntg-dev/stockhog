using System.Text.Json;
using Stockhog.Application.Assistant;
using Stockhog.Application.Tests.TestSupport;
using Stockhog.Application.Trading;
using Stockhog.Domain.Backtesting;
using Stockhog.Domain.Common;
using Stockhog.Domain.MarketData;
using Stockhog.Domain.Trading;

namespace Stockhog.Application.Tests.Assistant;

public class AssistantToolboxTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Every_tool_has_an_object_schema_and_required_fields_it_declares()
    {
        await using var host = new ApplicationTestHost();

        Assert.All(host.Toolbox.Tools, tool =>
        {
            Assert.Equal("object", tool.InputSchema["type"]!.GetValue<string>());
            var properties = tool.InputSchema["properties"]!.AsObject();
            Assert.All(tool.InputSchema["required"]!.AsArray(), r => Assert.True(properties.ContainsKey(r!.GetValue<string>()), $"{tool.Name}: {r}"));
        });
        Assert.Equal(host.Toolbox.Tools.Count, host.Toolbox.Tools.Select(t => t.Name).Distinct().Count());
    }

    [Fact]
    public async Task Ranking_is_returned_as_json_and_limited()
    {
        await using var host = new ApplicationTestHost();
        host.Database.Runs.Add(Completed("AAPL", 0, 12));
        host.Database.Runs.Add(Completed("MSFT", 1, 20));

        var result = await host.Toolbox.ExecuteAsync("get_run_ranking", Input("""{"limit": 1}"""), Ct);

        Assert.False(result.IsError);
        using var json = JsonDocument.Parse(result.Content);
        Assert.Equal(2, json.RootElement.GetProperty("totalRuns").GetInt32());
        var run = Assert.Single(json.RootElement.GetProperty("runs").EnumerateArray());
        Assert.Equal(("MSFT", 20m), (run.GetProperty("symbol").GetString(), run.GetProperty("netProfitPercent").GetDecimal()));
    }

    [Fact]
    public async Task Latest_prices_prefer_streamed_quotes()
    {
        await using var host = new ApplicationTestHost();
        host.Quotes.Quotes["AAPL"] = new Quote("AAPL", 189.9m, 190.1m, 190m, DateTimeOffset.UtcNow);
        host.LatestPrices.Price = 55m;

        var result = await host.Toolbox.ExecuteAsync("get_latest_prices", Input("""{"symbols": ["aapl", "msft"]}"""), Ct);

        var prices = JsonSerializer.Deserialize<List<LatestPrice>>(result.Content, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.Equal([new LatestPrice("AAPL", 190m), new LatestPrice("MSFT", 55m)], prices);
    }

    [Fact]
    public async Task Proposed_order_fills_the_ticket_and_nothing_is_placed()
    {
        await using var host = new ApplicationTestHost();

        var result = await host.Toolbox.ExecuteAsync("propose_order_ticket",
            Input("""{"symbol": "aapl", "side": "buy", "quantity": 5, "order_type": "limit", "limit_price": 185.5, "reason": "Best Sharpe"}"""), Ct);

        Assert.False(result.IsError);
        Assert.Equal(new OrderProposal("AAPL", OrderSide.Buy, OrderType.Limit, 5, 185.5m, "Best Sharpe"), Assert.Single(host.AssistantActions.Proposals));
        Assert.Empty(host.Broker.Orders);
        Assert.Empty(host.Database.Orders);
    }

    [Theory]
    [InlineData("""{"symbol": "AAPL", "side": "buy", "quantity": 5, "order_type": "limit", "reason": "x"}""", "limit_price")]
    [InlineData("""{"symbol": "AAPL", "side": "hold", "quantity": 5, "order_type": "market", "reason": "x"}""", "side")]
    [InlineData("""{"symbol": "AAPL", "side": "buy", "quantity": -1, "order_type": "market", "reason": "x"}""", "quantity")]
    [InlineData("""{"symbol": "NOT A SYMBOL", "side": "buy", "quantity": 1, "order_type": "market", "reason": "x"}""", "symbol")]
    [InlineData("""{"side": "buy", "quantity": 1, "order_type": "market", "reason": "x"}""", "symbol")]
    public async Task Invalid_proposals_are_returned_as_errors_the_model_can_fix(string input, string mentioned)
    {
        await using var host = new ApplicationTestHost();

        var result = await host.Toolbox.ExecuteAsync("propose_order_ticket", Input(input), Ct);

        Assert.True(result.IsError);
        Assert.Contains(mentioned, result.Content, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(host.AssistantActions.Proposals);
    }

    [Fact]
    public async Task Unknown_tool_and_failing_queries_are_errors_not_exceptions()
    {
        await using var host = new ApplicationTestHost();
        host.Reader.ReportFor = _ => null;

        var unknown = await host.Toolbox.ExecuteAsync("delete_everything", Input("{}"), Ct);
        var missingRun = await host.Toolbox.ExecuteAsync("get_run_details", Input("""{"symbol": "AAPL", "run_id": "nope"}"""), Ct);

        Assert.True(unknown.IsError);
        Assert.True(missingRun.IsError);
    }

    [Fact]
    public async Task Api_key_is_saved_removed_and_reported()
    {
        await using var host = new ApplicationTestHost();

        var saved = await host.Dispatcher.SendAsync(new SaveAssistantApiKeyCommand("  sk-ant-api03-abcdefghijklmnopqrstuvwxyz  "), Ct);
        Assert.Equal(new AssistantStatus(true, "test-model"), saved.Value);
        Assert.Equal("sk-ant-api03-abcdefghijklmnopqrstuvwxyz", host.AssistantCredentials.ApiKey);

        var invalid = await host.Dispatcher.SendAsync(new SaveAssistantApiKeyCommand("short"), Ct);
        Assert.True(invalid.IsFailure);

        var removed = await host.Dispatcher.SendAsync(new SaveAssistantApiKeyCommand(null), Ct);
        Assert.False(removed.Value.IsConfigured);
    }

    private static JsonElement Input(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static BacktestRun Completed(string ticker, int minutes, decimal netProfit)
    {
        var symbol = Symbol.Create(ticker);
        var strategy = StrategyName.Create("SmaCrossStrategy");
        var createdAt = TestData.Now.AddMinutes(minutes);
        var run = BacktestRun.Create(
            BacktestRunId.New(symbol, strategy, createdAt), symbol, strategy,
            DateRange.Create(new DateOnly(2026, 1, 2), new DateOnly(2026, 9, 30)), Money.Usd(100_000m),
            StrategyParameters.Create([new("fast-period", "26")]), createdAt);
        run.Start(createdAt);
        run.Complete(createdAt.AddSeconds(5), new PerformanceMetrics(netProfit, 5, 1.2m, 25, 55, 1.6m));
        return run;
    }
}
