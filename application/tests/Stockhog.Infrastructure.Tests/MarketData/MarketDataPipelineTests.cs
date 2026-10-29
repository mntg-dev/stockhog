using System.IO.Compression;
using System.Net;
using System.Text;
using System.Threading.RateLimiting;
using Stockhog.Application.MarketData;
using Stockhog.Domain.Common;
using Stockhog.Domain.MarketData;
using Stockhog.Infrastructure.Configuration;
using Stockhog.Infrastructure.MarketData;
using Stockhog.Infrastructure.Alpaca;
using Stockhog.Infrastructure.Lean.Data;
using Stockhog.Infrastructure.Tests.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Stockhog.Infrastructure.Tests.MarketData;

public class AlpacaRateLimitingHandlerTests
{
    [Fact]
    public async Task Requests_beyond_the_available_tokens_wait_for_replenishment()
    {
        // Tokens only come back when the test calls TryReplenish (which adds tokens in proportion to the elapsed time).
        using var limiter = new TokenBucketRateLimiter(new TokenBucketRateLimiterOptions
        {
            TokenLimit = 2,
            TokensPerPeriod = 1,
            ReplenishmentPeriod = TimeSpan.FromMilliseconds(1),
            QueueLimit = 10,
            AutoReplenishment = false
        });
        var inner = new CountingHandler();
        using var client = new HttpClient(new AlpacaRateLimitingHandler(new AlpacaRateLimiter(limiter)) { InnerHandler = inner });
        var ct = TestContext.Current.CancellationToken;

        var requests = Enumerable.Range(0, 3).Select(_ => client.GetAsync("http://alpaca.test/", ct)).ToList();
        await Task.Delay(200, ct);

        Assert.Equal(2, inner.Count);

        limiter.TryReplenish();
        await Task.WhenAll(requests);
        Assert.Equal(3, inner.Count);
    }

    private sealed class CountingHandler : HttpMessageHandler
    {
        private int _count;
        public int Count => _count;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _count);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }
}

public class MinuteBarSinkTests
{
    [Fact]
    public async Task A_day_split_across_pages_is_written_once_complete()
    {
        using var data = new TempDirectory();
        var ct = TestContext.Current.CancellationToken;
        var sink = new LeanEquityDataWriter().CreateSink(data.Path, "AAPL", Timeframe.Minute);
        var day1 = data.Combine("equity", "usa", "minute", "aapl", "20250902_trade.zip");
        var day2 = data.Combine("equity", "usa", "minute", "aapl", "20250903_trade.zip");

        await sink.AddAsync([Bar("2025-09-02T13:30:00Z"), Bar("2025-09-02T13:31:00Z"), Bar("2025-09-03T13:30:00Z")], ct);

        Assert.True(File.Exists(day1), "the first day is complete once a later day appears");
        Assert.False(File.Exists(day2), "the newest day may still continue on the next page");

        await sink.AddAsync([Bar("2025-09-03T13:31:00Z")], ct);
        var files = await sink.CompleteAsync(ct);

        Assert.Equal(2, files);
        Assert.Equal(2, ReadRows(day2).Length);
    }

    internal static MarketBar Bar(string utc) =>
        new(DateTime.Parse(utc, null, System.Globalization.DateTimeStyles.AdjustToUniversal), 1, 1, 1, 1, 1);

    internal static string[] ReadRows(string zipPath)
    {
        using var archive = ZipFile.OpenRead(zipPath);
        using var reader = new StreamReader(archive.Entries[0].Open());
        return reader.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries);
    }
}

/// <summary>
/// The producer/consumer downloader against stubbed Alpaca pages and a real Lean writer.
/// </summary>
public class MarketDataDownloaderTests
{
    private const string Page1 = """{"bars":[{"t":"2025-09-02T13:30:00Z","o":1,"h":1,"l":1,"c":1,"v":1},{"t":"2025-09-03T13:30:00Z","o":1,"h":1,"l":1,"c":1,"v":1}],"next_page_token":"p2"}""";
    private const string Page2 = """{"bars":[{"t":"2025-09-03T13:31:00Z","o":1,"h":1,"l":1,"c":1,"v":1},{"t":"2025-09-04T13:30:00Z","o":1,"h":1,"l":1,"c":1,"v":1}],"next_page_token":null}""";

    [Fact]
    public async Task Pages_flow_from_download_to_disk()
    {
        using var data = new TempDirectory();
        var downloader = CreateDownloader(data, new StubAlpaca(Page1, Page2));
        var progress = new List<int>();

        var result = await downloader.DownloadAsync(Request(), new SyncProgress(progress.Add), TestContext.Current.CancellationToken);

        Assert.Equal(new MarketDataDownloadResult("AAPL", 4, 3), result);
        Assert.Equal(2, MinuteBarSinkTests.ReadRows(data.Combine("equity", "usa", "minute", "aapl", "20250903_trade.zip")).Length);
        Assert.Equal(progress.Order(), progress);
        Assert.Equal(100, progress[^1]);
    }

    [Fact]
    public async Task A_failing_page_surfaces_the_http_error_not_a_cancellation()
    {
        using var data = new TempDirectory();
        var downloader = CreateDownloader(data, new StubAlpaca(Page1, null));

        var error = await Assert.ThrowsAsync<HttpRequestException>(() =>
            downloader.DownloadAsync(Request(), cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(HttpStatusCode.InternalServerError, error.StatusCode);
    }

    private static MarketDataRequest Request() =>
        new(Symbol.Create("AAPL"), Timeframe.Minute, DateRange.Create(new DateOnly(2025, 9, 2), new DateOnly(2025, 9, 4)));

    private static MarketDataDownloader CreateDownloader(TempDirectory data, HttpMessageHandler alpacaHandler)
    {
        var storage = Options.Create(new StorageOptions { DataFolder = data.Path, ResultsFolder = data.Path, DatabaseFile = data.Combine("x.db") });

        // Reference files already present, so the reference client makes no requests.
        foreach (var file in new[] { "market-hours/market-hours-database.json", "symbol-properties/symbol-properties-database.csv", "alternative/interest-rate/usa/interest-rate.csv" })
        {
            var path = data.Combine(file.Split('/'));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "{}");
        }

        var alpaca = new AlpacaMarketDataClient(
            new HttpClient(alpacaHandler) { BaseAddress = new Uri("https://alpaca.test/") },
            Options.Create(new AlpacaOptions { ApiKey = "k", ApiSecret = "s" }));
        var referenceData = new LeanReferenceDataClient(new HttpClient(new StubAlpaca()) { BaseAddress = new Uri("https://github.test/") }, storage);

        var store = new LeanMarketDataStore(new LeanEquityDataWriter(), referenceData, storage);
        return new MarketDataDownloader(alpaca, store, TimeProvider.System, NullLogger<MarketDataDownloader>.Instance);
    }

    /// <summary>Returns the pages in order; a <c>null</c> page is an HTTP 500.</summary>
    private sealed class StubAlpaca(params string?[] pages) : HttpMessageHandler
    {
        private int _next;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var page = pages[_next++];
            return Task.FromResult(page is null
                ? new HttpResponseMessage(HttpStatusCode.InternalServerError)
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(page, Encoding.UTF8, "application/json") });
        }
    }

    private sealed class SyncProgress(Action<int> report) : IProgress<int>
    {
        public void Report(int value) => report(value);
    }
}
