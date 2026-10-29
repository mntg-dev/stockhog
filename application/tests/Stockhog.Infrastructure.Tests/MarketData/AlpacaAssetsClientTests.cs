using System.Net;
using System.Text;
using Stockhog.Infrastructure.Alpaca;
using Stockhog.Infrastructure.Alpaca.Instruments;
using Microsoft.Extensions.Options;

namespace Stockhog.Infrastructure.Tests.MarketData;

public class AlpacaAssetsClientTests
{
    [Fact]
    public async Task Returns_tradable_assets_and_uses_the_trading_keys()
    {
        var handler = new StubHandler("""
            [
              {"id":"1","class":"us_equity","exchange":"NASDAQ","symbol":"AAPL","name":"Apple Inc. Common Stock","status":"active","tradable":true},
              {"id":"2","class":"us_equity","exchange":"NYSE","symbol":"OLD","name":"Not tradable","status":"active","tradable":false},
              {"id":"3","class":"us_equity","exchange":"ARCA","symbol":"SPY","name":null,"status":"active","tradable":true}
            ]
            """);
        var client = CreateClient(handler, new AlpacaOptions { ApiKey = "data-key", ApiSecret = "data-secret", TradingApiKey = "paper-key", TradingApiSecret = "paper-secret" });

        var instruments = await client.GetTradableInstrumentsAsync(TestContext.Current.CancellationToken);

        Assert.Equal(["AAPL", "SPY"], instruments.Select(i => i.Symbol));
        Assert.Equal(("Apple Inc. Common Stock", "NASDAQ"), (instruments[0].Name, instruments[0].Exchange));
        Assert.Equal(string.Empty, instruments[1].Name);

        var request = Assert.Single(handler.Requests);
        Assert.Equal("/v2/assets", request.RequestUri!.AbsolutePath);
        Assert.Contains("status=active", request.RequestUri.Query);
        Assert.Contains("asset_class=us_equity", request.RequestUri.Query);
        Assert.Equal("paper-key", request.Headers.GetValues("APCA-API-KEY-ID").Single());
    }

    [Fact]
    public async Task Is_not_configured_without_keys_and_sends_nothing()
    {
        var handler = new StubHandler();
        var client = CreateClient(handler, new AlpacaOptions());

        Assert.False(client.IsConfigured);
        await Assert.ThrowsAsync<AlpacaConfigurationException>(() => client.GetTradableInstrumentsAsync(TestContext.Current.CancellationToken));
        Assert.Empty(handler.Requests);
    }

    private static AlpacaAssetsClient CreateClient(HttpMessageHandler handler, AlpacaOptions options) =>
        new(new HttpClient(handler) { BaseAddress = new Uri("https://paper.example/") }, Options.Create(options));

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
}
