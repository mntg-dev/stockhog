using System.Net;
using System.Text;
using Stockhog.Application.Common;
using Stockhog.Infrastructure.Alpaca;
using Stockhog.Infrastructure.Alpaca.Trading;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace Stockhog.Infrastructure.Tests.Trading;

public class AlpacaMarketCalendarTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly AlpacaOptions WithKeys = new() { ApiKey = "PKDATA0000000000ABCD", ApiSecret = "secret" };

    [Fact]
    public async Task Sessions_are_read_once_with_early_closes_and_served_from_the_cache()
    {
        var handler = new StubHandler("""
            [{"date":"2026-11-25","open":"09:30","close":"16:00"},
             {"date":"2026-11-27","open":"09:30","close":"13:00"}]
            """);
        var calendar = Create(handler, WithKeys, new MarketCalendarCache());

        var week = await calendar.GetSessionsAsync(new DateOnly(2026, 11, 24), new DateOnly(2026, 11, 28), Ct);
        var friday = await calendar.GetSessionsAsync(new DateOnly(2026, 11, 27), new DateOnly(2026, 11, 27), Ct);

        Assert.Equal([new DateOnly(2026, 11, 25), new DateOnly(2026, 11, 27)], week!.Select(s => s.Date));   // Thanksgiving left out
        Assert.Equal(new TimeOnly(13, 0), Assert.Single(friday!).Close);
        var request = Assert.Single(handler.Requests);
        Assert.Equal("paper-api.alpaca.markets", request.Host);
        Assert.Contains("/v2/calendar?start=", request.PathAndQuery);
    }

    [Fact]
    public async Task Without_keys_or_an_answer_there_is_no_calendar()
    {
        var noKeys = Create(new StubHandler("[]"), new AlpacaOptions(), new MarketCalendarCache());
        var failing = Create(new StubHandler("""{"message":"forbidden"}""", HttpStatusCode.Forbidden), WithKeys, new MarketCalendarCache());

        Assert.Null(await noKeys.GetSessionsAsync(new DateOnly(2026, 11, 1), new DateOnly(2026, 11, 30), Ct));
        Assert.Null(await failing.GetSessionsAsync(new DateOnly(2026, 11, 1), new DateOnly(2026, 11, 30), Ct));
    }

    private static AlpacaMarketCalendar Create(StubHandler handler, AlpacaOptions options, MarketCalendarCache cache) =>
        new(new HttpClient(handler), Options.Create(options), cache, new FakeTimeProvider(new DateTimeOffset(2026, 11, 3, 15, 0, 0, TimeSpan.Zero)),
            NullLogger<AlpacaMarketCalendar>.Instance);

    private sealed class StubHandler(string body, HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!);
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }
}
