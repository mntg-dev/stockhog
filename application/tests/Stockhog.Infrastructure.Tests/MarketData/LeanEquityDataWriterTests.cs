using System.IO.Compression;
using Stockhog.Domain.MarketData;
using Stockhog.Infrastructure.MarketData;
using Stockhog.Infrastructure.Lean.Data;
using Stockhog.Infrastructure.Tests.TestSupport;

namespace Stockhog.Infrastructure.Tests.MarketData;

public class LeanEquityDataWriterTests
{
    private readonly LeanEquityDataWriter _writer = new();

    [Fact]
    public async Task Minute_bars_are_written_per_exchange_day_with_scaled_prices()
    {
        using var data = new TempDirectory();
        MarketBar[] bars =
        [
            // 09:30 New York (EDT, UTC-4)
            Bar("2025-09-02T13:30:00Z", 227.84m, 228.00m, 227.50m, 227.90m, 1500),
            // 21:00 New York on the same exchange day, although already 3 Sept in UTC
            Bar("2025-09-03T01:00:00Z", 228.10m, 228.20m, 228.00m, 228.15m, 10),
            Bar("2025-09-03T13:30:00Z", 230m, 231m, 229m, 230.5m, 2000)
        ];

        var files = await _writer.WriteAsync(data.Path, "AAPL", Timeframe.Minute, bars, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(2, files);
        var day1 = ReadEntry(data.Combine("equity", "usa", "minute", "aapl", "20250902_trade.zip"), "20250902_aapl_minute_trade.csv");
        Assert.Equal(
        [
            "34200000,2278400,2280000,2275000,2279000,1500",
            "75600000,2281000,2282000,2280000,2281500,10"
        ], day1);
        Assert.True(File.Exists(data.Combine("equity", "usa", "minute", "aapl", "20250903_trade.zip")));
    }

    [Fact]
    public async Task Daily_bars_are_merged_with_existing_rows()
    {
        using var data = new TempDirectory();
        var ct = TestContext.Current.CancellationToken;

        await _writer.WriteAsync(data.Path, "msft", Timeframe.Daily,
            [Bar("2025-09-03T04:00:00Z", 1, 2, 0.5m, 1.5m, 100), Bar("2025-09-02T04:00:00Z", 1, 1, 1, 1, 1)], cancellationToken: ct);
        // Second download overlaps one day (replaced) and adds a new one.
        await _writer.WriteAsync(data.Path, "msft", Timeframe.Daily,
            [Bar("2025-09-03T04:00:00Z", 3, 3, 3, 3, 300), Bar("2025-09-04T04:00:00Z", 4, 4, 4, 4, 400)], cancellationToken: ct);

        var rows = ReadEntry(data.Combine("equity", "usa", "daily", "msft.zip"), "msft.csv");
        Assert.Equal(
        [
            "20250902 00:00,10000,10000,10000,10000,1",
            "20250903 00:00,30000,30000,30000,30000,300",
            "20250904 00:00,40000,40000,40000,40000,400"
        ], rows);
    }

    [Fact]
    public async Task Hour_bars_use_exchange_time()
    {
        using var data = new TempDirectory();

        await _writer.WriteAsync(data.Path, "AMZN", Timeframe.Hour, [Bar("2025-12-01T15:00:00Z", 1, 1, 1, 1, 5)],
            cancellationToken: TestContext.Current.CancellationToken);

        // December is EST (UTC-5).
        Assert.Equal(["20251201 10:00,10000,10000,10000,10000,5"], ReadEntry(data.Combine("equity", "usa", "hour", "amzn.zip"), "amzn.csv"));
    }

    [Fact]
    public async Task No_temporary_files_are_left_behind()
    {
        using var data = new TempDirectory();

        await _writer.WriteAsync(data.Path, "AAPL", Timeframe.Daily, [Bar("2025-09-02T04:00:00Z", 1, 1, 1, 1, 1)],
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Empty(Directory.EnumerateFiles(data.Path, "*.tmp", SearchOption.AllDirectories));
    }

    private static MarketBar Bar(string utc, decimal open, decimal high, decimal low, decimal close, long volume) =>
        new(DateTime.Parse(utc, null, System.Globalization.DateTimeStyles.AdjustToUniversal), open, high, low, close, volume);

    private static string[] ReadEntry(string zipPath, string entryName)
    {
        using var archive = ZipFile.OpenRead(zipPath);
        var entry = archive.GetEntry(entryName) ?? throw new Xunit.Sdk.XunitException($"Entry {entryName} missing in {zipPath}");
        using var reader = new StreamReader(entry.Open());
        return reader.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries);
    }
}
