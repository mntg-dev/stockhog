using System.Net;
using System.Text;
using Stockhog.Application.Notifications;
using Stockhog.Infrastructure.Notifications;
using Microsoft.Extensions.Logging.Abstractions;

namespace Stockhog.Infrastructure.Tests.Notifications;

public sealed class TelegramChannelTests : IDisposable
{
    private const string Token = "123456789:AAEhBOweik6ad9r_QXMENQjcrGbqCr4K-ab";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly string _path = Path.Combine(Path.GetTempPath(), $"stockhog-telegram-{Guid.NewGuid():N}.bin");

    public void Dispose() => File.Delete(_path);

    [Fact]
    public async Task A_message_is_posted_to_the_bot_api_for_the_chat()
    {
        var handler = new StubHandler(_ => (HttpStatusCode.OK, """{"ok":true}"""));

        var problem = await Channel(handler).SendAsync(new ChatTarget(Token, "987654321"), "Bought 10 AAPL", Ct);

        Assert.Null(problem);
        var (uri, body) = Assert.Single(handler.Requests);
        Assert.Equal("api.telegram.org", uri.Host);
        Assert.EndsWith("/sendMessage", uri.AbsolutePath);
        Assert.Contains("\"chat_id\":\"987654321\"", body);
        Assert.Contains("Bought 10 AAPL", body);
    }

    [Fact]
    public async Task A_refusal_returns_telegrams_description_and_never_the_token()
    {
        var refused = Channel(new StubHandler(_ => (HttpStatusCode.BadRequest, """{"ok":false,"description":"Bad Request: chat not found"}""")));
        var offline = Channel(new StubHandler(_ => throw new HttpRequestException($"Connection to https://api.telegram.org/bot{Token}/sendMessage failed")));

        var refusal = await refused.SendAsync(new ChatTarget(Token, "1"), "x", Ct);
        var unreachable = await offline.SendAsync(new ChatTarget(Token, "1"), "x", Ct);

        Assert.Equal("Bad Request: chat not found", refusal);
        Assert.Equal("Telegram could not be reached.", unreachable);
    }

    [Fact]
    public void The_target_is_kept_encrypted_and_removed_on_request()
    {
        var channel = Channel(new StubHandler(_ => (HttpStatusCode.OK, "{}")));

        channel.Save(new ChatTarget(Token, "987654321"));
        var bytes = File.ReadAllBytes(_path);
        var reloaded = Channel(new StubHandler(_ => (HttpStatusCode.OK, "{}")));
        var target = reloaded.Target;
        reloaded.Save(null);

        Assert.Equal(new ChatTarget(Token, "987654321"), target);
        Assert.DoesNotContain("AAEhBOweik", Encoding.UTF8.GetString(bytes));
        Assert.False(File.Exists(_path));
        Assert.False(reloaded.IsConfigured);
    }

    private TelegramChannel Channel(StubHandler handler) => new(new SingleClientFactory(handler), NullLogger<TelegramChannel>.Instance, _path);

    private sealed class SingleClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class StubHandler(Func<Uri, (HttpStatusCode Status, string Body)> respond) : HttpMessageHandler
    {
        public List<(Uri Uri, string Body)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add((request.RequestUri!, body));
            var (status, text) = respond(request.RequestUri!);
            return new HttpResponseMessage(status) { Content = new StringContent(text, Encoding.UTF8, "application/json") };
        }
    }
}
