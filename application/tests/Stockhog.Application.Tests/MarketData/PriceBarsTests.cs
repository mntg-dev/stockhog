using Stockhog.Application.Abstractions.Results;
using Stockhog.Application.MarketData.DownloadMarketData;
using Stockhog.Application.MarketData.GetPriceBars;
using Stockhog.Application.Tests.TestSupport;
using Stockhog.Domain.MarketData;

namespace Stockhog.Application.Tests.MarketData;

public class PriceBarsTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly GetPriceBarsQuery Query =
        new("aapl", BarResolution.Daily, new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 10));

    [Fact]
    public async Task Bars_are_cached_per_symbol_and_period()
    {
        await using var host = new ApplicationTestHost();

        var first = await host.Dispatcher.SendAsync(Query, Ct);
        var second = await host.Dispatcher.SendAsync(Query with { Symbol = "AAPL" }, Ct);

        Assert.Equal(10, first.Value.Bars.Count);
        Assert.Same(first.Value, second.Value);
        Assert.Equal(1, host.Prices.Reads);
    }

    [Fact]
    public async Task Downloading_new_data_for_the_symbol_invalidates_its_cached_bars()
    {
        await using var host = new ApplicationTestHost();
        await host.Dispatcher.SendAsync(Query, Ct);

        // The completed download raises MarketDataDownloaded, whose handler removes the symbol's price tag.
        await host.Dispatcher.SendAsync(new DownloadMarketDataCommand("AAPL", Timeframe.Minute, new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 10)), Ct);
        await host.Dispatcher.SendAsync(Query, Ct);

        Assert.Equal(2, host.Prices.Reads);
    }

    [Fact]
    public async Task Other_symbols_stay_cached()
    {
        await using var host = new ApplicationTestHost();
        await host.Dispatcher.SendAsync(Query, Ct);

        await host.Dispatcher.SendAsync(new DownloadMarketDataCommand("MSFT", Timeframe.Daily, new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 10)), Ct);
        await host.Dispatcher.SendAsync(Query, Ct);

        Assert.Equal(1, host.Prices.Reads);
    }

    [Fact]
    public async Task Reversed_period_is_rejected()
    {
        await using var host = new ApplicationTestHost();

        var result = await host.Dispatcher.SendAsync(Query with { Start = new DateOnly(2026, 11, 1) }, Ct);

        Assert.Equal(ErrorType.Validation, result.Error?.Type);
    }
}
