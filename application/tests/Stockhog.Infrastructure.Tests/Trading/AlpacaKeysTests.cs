using System.Net;
using System.Text;
using Stockhog.Application.Brokers;
using Stockhog.Domain.Trading;
using Stockhog.Infrastructure.Alpaca;
using Microsoft.Extensions.Options;

namespace Stockhog.Infrastructure.Tests.Trading;

public sealed class AlpacaKeysTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly string _path = Path.Combine(Path.GetTempPath(), $"stockhog-keys-{Guid.NewGuid():N}.bin");

    public void Dispose() => File.Delete(_path);

    [Fact]
    public void Keys_round_trip_encrypted_and_are_gone_once_removed()
    {
        var file = new ProtectedAlpacaKeyFile(_path);
        var keys = new BrokerKeys("PKDATA0000000000ABCD", "secret-0000000000000000000000000000000001", "PKPAPER000000000WXYZ", "paper-secret-000000000000000000000000001");

        file.Write(keys);
        var bytes = File.ReadAllBytes(_path);
        var read = new ProtectedAlpacaKeyFile(_path).Read();
        file.Write(null);

        Assert.Equal(keys, read);
        Assert.DoesNotContain("secret-0000", Encoding.UTF8.GetString(bytes));
        Assert.DoesNotContain("PKDATA", Encoding.UTF8.GetString(bytes));
        Assert.Null(file.Read());
        Assert.False(File.Exists(_path));
    }

    [Fact]
    public void A_damaged_file_counts_as_no_keys()
    {
        File.WriteAllBytes(_path, [1, 2, 3]);

        Assert.Null(new ProtectedAlpacaKeyFile(_path).Read());
    }

    [Fact]
    public void Stored_keys_win_over_configuration_but_never_touch_the_live_keys()
    {
        var file = new ProtectedAlpacaKeyFile(_path);
        file.Write(new BrokerKeys("PKSTORED000000000001", "stored-secret-00000000000000000000000001"));
        var startup = new StoredAlpacaKeys(file);
        var options = new AlpacaOptions { ApiKey = "config", ApiSecret = "config-secret", TradingApiKey = "config-paper", LiveApiKey = "live", LiveApiSecret = "live-secret" };

        startup.PostConfigure(null, options);

        Assert.Equal(("PKSTORED000000000001", (string?)null), (options.ApiKey, options.TradingApiKey));
        Assert.Equal(("live", "live-secret"), (options.LiveApiKey, options.LiveApiSecret));
        Assert.Equal(BrokerKeySource.Settings, startup.Source);
    }

    [Fact]
    public void Without_stored_keys_the_configuration_is_kept_and_reported_as_the_source()
    {
        var startup = new StoredAlpacaKeys(new ProtectedAlpacaKeyFile(_path));
        var configured = new AlpacaOptions { ApiKey = "config", ApiSecret = "config-secret" };
        var empty = new AlpacaOptions();

        startup.PostConfigure(null, configured);
        var fromConfig = startup.Source;
        startup.PostConfigure(null, empty);

        Assert.Equal("config", configured.ApiKey);
        Assert.Equal((BrokerKeySource.Configuration, BrokerKeySource.None), (fromConfig, startup.Source));
    }

    [Fact]
    public async Task The_test_reads_market_data_and_the_paper_account_only_even_in_live_mode()
    {
        var handler = new StubHandler(request => request.Uri.Host == "data.alpaca.markets"
            ? (HttpStatusCode.OK, """{"bars":{}}""")
            : (HttpStatusCode.OK, """{"account_number":"PA123"}"""));
        var tester = new AlpacaKeyTester(new HttpClient(handler), Options.Create(new AlpacaOptions { TradingMode = TradingMode.Live }));

        var check = await tester.TestAsync(new BrokerKeys("PKDATA0000000000ABCD", "data-secret", "PKPAPER000000000WXYZ", "paper-secret"), Ct);

        Assert.True(check.IsOk);
        Assert.Equal("PA123", check.AccountNumber);
        Assert.Equal(["data.alpaca.markets", "paper-api.alpaca.markets"], handler.Requests.Select(r => r.Uri.Host));
        Assert.Equal(["PKDATA0000000000ABCD", "PKPAPER000000000WXYZ"], handler.Requests.Select(r => r.KeyId));
    }

    [Fact]
    public async Task Refused_keys_and_unreachable_apis_are_reported_per_api()
    {
        var handler = new StubHandler(request => request.Uri.Host == "data.alpaca.markets"
            ? (HttpStatusCode.Unauthorized, """{"message":"forbidden"}""")
            : throw new HttpRequestException("No such host is known."));
        var tester = new AlpacaKeyTester(new HttpClient(handler), Options.Create(new AlpacaOptions()));

        var check = await tester.TestAsync(new BrokerKeys("PKDATA0000000000ABCD", "data-secret"), Ct);

        Assert.False(check.IsOk);
        Assert.Equal("the key or secret was not accepted.", check.DataError);
        Assert.Contains("could not be reached", check.TradingError);
        Assert.Null(check.AccountNumber);
    }

    private sealed record SentRequest(Uri Uri, string? KeyId);

    private sealed class StubHandler(Func<SentRequest, (HttpStatusCode Status, string Body)> respond) : HttpMessageHandler
    {
        public List<SentRequest> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var sent = new SentRequest(request.RequestUri!, request.Headers.TryGetValues("APCA-API-KEY-ID", out var keys) ? keys.Single() : null);
            Requests.Add(sent);
            var (status, body) = respond(sent);
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }
}
