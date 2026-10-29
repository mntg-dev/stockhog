using CommunityToolkit.Mvvm.Messaging;
using Stockhog.Application.Abstractions.Messaging;
using Stockhog.Application.Abstractions.Results;
using Stockhog.Application.Instruments;
using Stockhog.Application.MarketData;
using Stockhog.Application.MarketData.GetMarketDataCoverage;
using Stockhog.Domain.Common;
using Stockhog.Domain.MarketData;
using Stockhog.Presentation.Instruments;
using Stockhog.Presentation.Shell;
using Microsoft.Extensions.Logging.Abstractions;

namespace Stockhog.Presentation.Tests;

public class DownloadedSymbolCatalogTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Only_symbols_with_stored_data_are_offered_with_names_and_what_is_stored()
    {
        var (catalog, dispatcher, _) = await CreateAsync();
        dispatcher.Coverage.AddRange(
        [
            Coverage("SPY", Timeframe.Daily, 1, 30),
            Coverage("AAPL", Timeframe.Minute, 5, 20),
            Coverage("AAPL", Timeframe.Daily, 1, 30),
            new MarketDataCoverage("EMPTY", Timeframe.Daily, [], null)
        ]);

        await catalog.LoadAsync(Ct);
        var all = catalog.Suggest(string.Empty, 100);

        Assert.Equal(["AAPL", "SPY"], all.Select(s => s.Symbol));
        Assert.Equal("Apple Inc.", all[0].Name);
        Assert.Contains("2026-01-05..2026-01-20", all[0].Detail);
        Assert.Contains("2026-01-01..2026-01-30", all[0].Detail);
        // SPY is not in the instrument list, so it has no company name.
        Assert.Equal(string.Empty, all[1].Name);
    }

    [Fact]
    public async Task Typed_text_is_searched_like_the_full_list()
    {
        var (catalog, dispatcher, _) = await CreateAsync();
        dispatcher.Coverage.AddRange([Coverage("AAPL", Timeframe.Minute, 1, 2), Coverage("MSFT", Timeframe.Minute, 1, 2)]);
        await catalog.LoadAsync(Ct);

        Assert.Equal(["AAPL"], catalog.Suggest("apple", 10).Select(s => s.Symbol));
    }

    [Fact]
    public async Task Finished_downloads_refresh_the_list()
    {
        var (catalog, dispatcher, messenger) = await CreateAsync();
        await catalog.LoadAsync(Ct);
        Assert.Equal(0, catalog.Count);

        dispatcher.Coverage.Add(Coverage("AAPL", Timeframe.Minute, 1, 2));
        messenger.Send(new MarketDataChangedMessage());

        Assert.Equal(1, catalog.Count);
    }

    [Fact]
    public async Task Full_instrument_list_is_browsed_in_symbol_order()
    {
        var (_, _, _, instruments) = await CreateWithInstrumentsAsync();

        Assert.Equal(["AAPL", "MSFT", "NVDA"], instruments.Suggest(string.Empty, 100).Select(s => s.Symbol));
        Assert.Equal(2, instruments.Suggest(" ", 2).Count);
    }

    private static async Task<(DownloadedSymbolCatalog, StubDispatcher, IMessenger)> CreateAsync()
    {
        var (catalog, dispatcher, messenger, _) = await CreateWithInstrumentsAsync();
        return (catalog, dispatcher, messenger);
    }

    private static async Task<(DownloadedSymbolCatalog, StubDispatcher, IMessenger, InstrumentCatalog)> CreateWithInstrumentsAsync()
    {
        var dispatcher = new StubDispatcher();
        dispatcher.Instruments.AddRange(
        [
            new InstrumentInfo("AAPL", "Apple Inc.", "NASDAQ"),
            new InstrumentInfo("MSFT", "Microsoft Corporation", "NASDAQ"),
            new InstrumentInfo("NVDA", "NVIDIA Corporation", "NASDAQ")
        ]);
        var messenger = new WeakReferenceMessenger();
        var instruments = new InstrumentCatalog(dispatcher, messenger, NullLogger<InstrumentCatalog>.Instance);
        await instruments.LoadAsync(TestContext.Current.CancellationToken);

        return (new DownloadedSymbolCatalog(dispatcher, messenger, instruments, NullLogger<DownloadedSymbolCatalog>.Instance), dispatcher, messenger, instruments);
    }

    private static MarketDataCoverage Coverage(string symbol, Timeframe timeframe, int fromDay, int toDay) =>
        new(symbol, timeframe, [DateRange.Create(new DateOnly(2026, 1, fromDay), new DateOnly(2026, 1, toDay))], null);

    /// <summary>Answers the two queries the catalogs send.</summary>
    private sealed class StubDispatcher : IDispatcher
    {
        public List<MarketDataCoverage> Coverage { get; } = [];

        public List<InstrumentInfo> Instruments { get; } = [];

        public Task<Result<TResponse>> SendAsync<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
        {
            object value = request switch
            {
                GetMarketDataCoverageQuery => (IReadOnlyList<MarketDataCoverage>)Coverage.ToList(),
                GetInstrumentsQuery => (IReadOnlyList<InstrumentInfo>)Instruments.ToList(),
                _ => throw new NotSupportedException(request.GetType().Name)
            };
            return Task.FromResult(Result<TResponse>.Success((TResponse)value));
        }
    }
}
