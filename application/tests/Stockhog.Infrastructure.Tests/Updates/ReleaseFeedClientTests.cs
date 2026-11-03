using System.Net;
using System.Text;
using Stockhog.Infrastructure.Updates;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Stockhog.Infrastructure.Tests.Updates;

public class ReleaseFeedClientTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Reads_the_version_and_installer_of_the_release_manifest()
    {
        var handler = new StubHandler(HttpStatusCode.OK, """
            {"version":"v0.4.0","publishedAt":"2026-11-03T10:00:00Z",
             "msi":{"name":"Stockhog-v0.4.0-win-x64.msi","url":"https://emtradingstorage.blob.core.windows.net/releases/v0.4.0/Stockhog-v0.4.0-win-x64.msi","sha256Url":"x","size":1},
             "zip":{"name":"z","url":"https://example.com/z.zip","sha256Url":"x","size":1}}
            """);

        var latest = await Client(handler, new UpdateOptions()).GetLatestAsync(Ct);

        Assert.Equal("v0.4.0", latest!.Version);
        Assert.Equal("/releases/v0.4.0/Stockhog-v0.4.0-win-x64.msi", latest.InstallerUrl!.AbsolutePath);
        Assert.Equal("https://emtradingstorage.blob.core.windows.net/releases/latest.json", handler.Requests.Single().AbsoluteUri);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound, "")]
    [InlineData(HttpStatusCode.OK, "not json")]
    [InlineData(HttpStatusCode.OK, """{"msi":null}""")]
    public async Task A_missing_or_unreadable_manifest_means_no_update(HttpStatusCode status, string body) =>
        Assert.Null(await Client(new StubHandler(status, body), new UpdateOptions()).GetLatestAsync(Ct));

    [Fact]
    public async Task Nothing_is_requested_when_the_check_is_off()
    {
        var handler = new StubHandler(HttpStatusCode.OK, "{}");

        Assert.Null(await Client(handler, new UpdateOptions { CheckOnStart = false }).GetLatestAsync(Ct));
        Assert.Empty(handler.Requests);
    }

    private static ReleaseFeedClient Client(StubHandler handler, UpdateOptions options) =>
        new(new HttpClient(handler), Options.Create(options), NullLogger<ReleaseFeedClient>.Instance);

    private sealed class StubHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!);
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }
}
