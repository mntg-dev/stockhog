using System.Net;
using System.Text;
using Stockhog.Domain.MarketData;
using Stockhog.Infrastructure.Configuration;
using Stockhog.Infrastructure.Alpaca;
using Microsoft.Extensions.Options;

namespace Stockhog.Infrastructure.Tests.MarketData;

public class AlpacaMarketDataClientTests
{
    private static readonly DateTime Start = new(2025, 9, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime End = new(2025, 9, 3, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task Follows_page_tokens_and_sends_credentials()
    {
        var handler = new StubHandler(
            """{"bars":[{"t":"2025-09-01T13:30:00Z","o":1.5,"h":2,"l":1,"c":1.75,"v":10}],"symbol":"AAPL","next_page_token":"page-2"}""",
            """{"bars":[{"t":"2025-09-02T13:30:00Z","o":2,"h":3,"l":2,"c":2.5,"v":20}],"symbol":"AAPL","next_page_token":null}""");
        var client = CreateClient(handler, new AlpacaOptions { ApiKey = "key", ApiSecret = "secret" });
        var coverage = new List<double>();

        var bars = await client.GetBarsAsync("AAPL", Timeframe.Minute, Start, End, new SyncProgress(coverage.Add), TestContext.Current.CancellationToken);

        Assert.Equal(2, bars.Count);
        Assert.Equal(new DateTime(2025, 9, 2, 13, 30, 0, DateTimeKind.Utc), bars[1].TimeUtc);
        Assert.Equal(2.5m, bars[1].Close);

        Assert.Equal(2, handler.Requests.Count);
        Assert.DoesNotContain("page_token", handler.Requests[0].RequestUri!.Query);
        Assert.Contains("page_token=page-2", handler.Requests[1].RequestUri!.Query);
        Assert.Contains("timeframe=1Min", handler.Requests[0].RequestUri!.Query);
        Assert.All(handler.Requests, r => Assert.Equal("key", r.Headers.GetValues("APCA-API-KEY-ID").Single()));

        Assert.Equal(1, coverage[^1]);
        Assert.True(coverage.SequenceEqual(coverage.Order()), "coverage should never go backwards");
    }

    [Fact]
    public async Task Missing_credentials_fail_before_any_request()
    {
        var handler = new StubHandler();
        var client = CreateClient(handler, new AlpacaOptions());

        await Assert.ThrowsAsync<AlpacaConfigurationException>(() =>
            client.GetBarsAsync("AAPL", Timeframe.Daily, Start, End, null, TestContext.Current.CancellationToken));
        Assert.Empty(handler.Requests);
    }

    private static AlpacaMarketDataClient CreateClient(HttpMessageHandler handler, AlpacaOptions options) =>
        new(new HttpClient(handler) { BaseAddress = new Uri("https://data.example/") }, Options.Create(options));

    private sealed class StubHandler(params string[] responses) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responses[Requests.Count - 1], Encoding.UTF8, "application/json")
            });
        }
    }

    private sealed class SyncProgress(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }
}
