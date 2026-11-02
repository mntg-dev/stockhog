using System.Net;
using System.Text;
using Stockhog.Application.Screening;
using Stockhog.Infrastructure.Alpaca;
using Stockhog.Infrastructure.Alpaca.MarketData;
using Microsoft.Extensions.Options;

namespace Stockhog.Infrastructure.Tests.MarketData;

public class AlpacaDailyBarsClientTests
{
    private static readonly DateOnly Start = new(2026, 10, 12);
    private static readonly DateOnly End = new(2026, 10, 14);

    [Fact]
    public async Task Collects_each_symbols_bars_across_pages_as_exchange_dates()
    {
        // The page limit counts bars of all symbols, so one symbol's bars can continue on the next page.
        var handler = new StubHandler(
            """
            {"bars":{
              "AAPL":[{"t":"2026-10-12T04:00:00Z","o":1,"h":2,"l":0.5,"c":1.5,"v":100},{"t":"2026-10-13T04:00:00Z","o":1.5,"h":2.5,"l":1,"c":2,"v":200}],
              "MSFT":[{"t":"2026-10-12T04:00:00Z","o":10,"h":11,"l":9,"c":10.5,"v":300}]
            },"next_page_token":"page-2"}
            """,
            """
            {"bars":{"MSFT":[{"t":"2026-10-13T04:00:00Z","o":10.5,"h":12,"l":10,"c":11,"v":400}]},"next_page_token":null}
            """);
        var client = CreateClient(handler, new AlpacaOptions { ApiKey = "key", ApiSecret = "secret" });

        var bars = await client.GetDailyBarsAsync(["AAPL", "MSFT", "NODATA"], Start, End, TestContext.Current.CancellationToken);

        Assert.Equal(["AAPL", "MSFT"], bars.Keys.Order());
        Assert.Equal(new DailyBar(new DateOnly(2026, 10, 13), 1.5m, 2.5m, 1m, 2m, 200), bars["AAPL"][1]);
        Assert.Equal([new DateOnly(2026, 10, 12), new DateOnly(2026, 10, 13)], bars["MSFT"].Select(b => b.Date));

        var first = Uri.UnescapeDataString(handler.Requests[0].RequestUri!.Query);
        Assert.StartsWith("?symbols=AAPL,MSFT,NODATA&timeframe=1Day", first);
        Assert.Contains("start=2026-10-12T00:00:00Z", first);
        Assert.Contains("end=2026-10-15T00:00:00Z", first);   // midnight UTC after the last day includes its 04:00 UTC bar
        Assert.Contains("adjustment=split", first);
        Assert.Contains("feed=sip", first);
        Assert.Contains("page_token=page-2", handler.Requests[1].RequestUri!.Query);
        Assert.All(handler.Requests, r => Assert.Equal("key", r.Headers.GetValues("APCA-API-KEY-ID").Single()));
    }

    [Fact]
    public async Task A_refused_feed_explains_how_to_switch_it()
    {
        var handler = new StubHandler(HttpStatusCode.Forbidden, """{"message":"subscription does not permit querying recent SIP data"}""");
        var client = CreateClient(handler, new AlpacaOptions { ApiKey = "key", ApiSecret = "secret" });

        var error = await Assert.ThrowsAsync<HttpRequestException>(() =>
            client.GetDailyBarsAsync(["AAPL"], Start, End, TestContext.Current.CancellationToken));

        Assert.Contains("Alpaca:ScreenerFeed", error.Message);
    }

    [Fact]
    public async Task Missing_credentials_fail_before_any_request()
    {
        var handler = new StubHandler();
        var client = CreateClient(handler, new AlpacaOptions());

        Assert.False(client.IsConfigured);
        await Assert.ThrowsAsync<AlpacaConfigurationException>(() =>
            client.GetDailyBarsAsync(["AAPL"], Start, End, TestContext.Current.CancellationToken));
        Assert.Empty(handler.Requests);
    }

    private static AlpacaDailyBarsClient CreateClient(HttpMessageHandler handler, AlpacaOptions options) =>
        new(new HttpClient(handler) { BaseAddress = new Uri("https://data.example/") }, Options.Create(options));

    private sealed class StubHandler(HttpStatusCode status, params string[] responses) : HttpMessageHandler
    {
        public StubHandler(params string[] responses) : this(HttpStatusCode.OK, responses)
        {
        }

        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(responses[Requests.Count - 1], Encoding.UTF8, "application/json")
            });
        }
    }
}
