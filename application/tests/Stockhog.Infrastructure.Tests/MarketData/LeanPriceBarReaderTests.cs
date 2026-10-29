using Stockhog.Application.MarketData.GetPriceBars;
using Stockhog.Domain.Common;
using Stockhog.Domain.MarketData;
using Stockhog.Infrastructure.Configuration;
using Stockhog.Infrastructure.Lean.Data;
using Stockhog.Infrastructure.MarketData;
using Stockhog.Infrastructure.Tests.TestSupport;
using Microsoft.Extensions.Options;

namespace Stockhog.Infrastructure.Tests.MarketData;

/// <summary>
/// Writes data with the real Lean writer and reads it back, so both sides of the format agree.
/// </summary>
public class LeanPriceBarReaderTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly DateRange September = DateRange.Create(new DateOnly(2025, 9, 1), new DateOnly(2025, 9, 30));

    [Fact]
    public async Task Minute_data_is_aggregated_to_daily_bars()
    {
        using var data = new TempDirectory();
        await WriteAsync(data, Timeframe.Minute,
            Bar("2025-09-02T13:30:00Z", 10, 12, 9, 11, 100),   // 09:30 New York
            Bar("2025-09-02T19:59:00Z", 11, 15, 8, 14, 50),    // 15:59
            Bar("2025-09-03T13:30:00Z", 20, 21, 19, 20, 10));

        var bars = await Reader(data).ReadAsync(Symbol.Create("AAPL"), BarResolution.Daily, September, Ct);

        Assert.Equal(2, bars.Count);
        Assert.Equal(new PriceBarData(new DateTime(2025, 9, 2), 10, 15, 8, 14, 150), bars[0]);
        Assert.Equal(new DateTime(2025, 9, 3), bars[1].Time);
    }

    [Fact]
    public async Task Minute_data_is_aggregated_to_hourly_bars_in_exchange_time()
    {
        using var data = new TempDirectory();
        await WriteAsync(data, Timeframe.Minute,
            Bar("2025-09-02T13:30:00Z", 10, 11, 10, 11, 1),
            Bar("2025-09-02T13:59:00Z", 11, 13, 11, 12, 1),
            Bar("2025-09-02T14:00:00Z", 12, 12, 7, 8, 1));

        var bars = await Reader(data).ReadAsync(Symbol.Create("AAPL"), BarResolution.Hourly, September, Ct);

        Assert.Equal([new DateTime(2025, 9, 2, 9, 0, 0), new DateTime(2025, 9, 2, 10, 0, 0)], bars.Select(b => b.Time));
        Assert.Equal((10m, 13m, 12m), (bars[0].Open, bars[0].High, bars[0].Close));
    }

    [Fact]
    public async Task Stored_daily_file_is_preferred_and_filtered_by_period()
    {
        using var data = new TempDirectory();
        await WriteAsync(data, Timeframe.Daily,
            Bar("2025-08-29T04:00:00Z", 1, 1, 1, 1, 1),
            Bar("2025-09-02T04:00:00Z", 227.84m, 230, 226.5m, 229.1m, 1_000));

        var bars = await Reader(data).ReadAsync(Symbol.Create("AAPL"), BarResolution.Daily, September, Ct);

        Assert.Equal(new PriceBarData(new DateTime(2025, 9, 2), 227.84m, 230m, 226.5m, 229.1m, 1_000), Assert.Single(bars));
    }

    [Fact]
    public async Task Unknown_symbol_has_no_bars()
    {
        using var data = new TempDirectory();

        Assert.Empty(await Reader(data).ReadAsync(Symbol.Create("ZZZZ"), BarResolution.Daily, September, Ct));
    }

    private static LeanPriceBarReader Reader(TempDirectory data) =>
        new(Options.Create(new StorageOptions { DataFolder = data.Path, ResultsFolder = data.Path, DatabaseFile = data.Combine("x.db") }));

    private static Task WriteAsync(TempDirectory data, Timeframe timeframe, params MarketBar[] bars) =>
        new LeanEquityDataWriter().WriteAsync(data.Path, "AAPL", timeframe, bars, cancellationToken: Ct);

    private static MarketBar Bar(string utc, decimal open, decimal high, decimal low, decimal close, long volume) =>
        new(DateTime.Parse(utc, null, System.Globalization.DateTimeStyles.AdjustToUniversal), open, high, low, close, volume);
}
