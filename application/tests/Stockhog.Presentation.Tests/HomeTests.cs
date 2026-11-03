using CommunityToolkit.Mvvm.Messaging;
using Stockhog.Application.Abstractions.Messaging;
using Stockhog.Application.Abstractions.Results;
using Stockhog.Application.Autopilot;
using Stockhog.Application.Backtesting;
using Stockhog.Application.Backtesting.GetBacktestHistory;
using Stockhog.Application.MarketData;
using Stockhog.Application.MarketData.GetMarketDataCoverage;
using Stockhog.Application.Proposals;
using Stockhog.Application.Scanning;
using Stockhog.Domain.Autopilot;
using Stockhog.Domain.Common;
using Stockhog.Presentation.Home;
using Stockhog.Presentation.Settings;
using Stockhog.Presentation.Shell;
using Microsoft.Extensions.Logging.Abstractions;

namespace Stockhog.Presentation.Tests;

public class HomeTests
{
    [Fact]
    public async Task A_fresh_install_shows_every_step_still_to_do_and_no_account()
    {
        var home = Home(new StubDispatcher(), keys: false);

        await home.RefreshCommand.ExecuteAsync(null);

        Assert.Equal(5, home.Steps.Count);
        Assert.All(home.Steps, s => Assert.False(s.IsDone));
        Assert.False(home.IsSetUp);
        Assert.Equal(typeof(SettingsViewModel), home.Steps[0].Page);
        Assert.False(string.IsNullOrWhiteSpace(home.AccountText));
        Assert.Equal(0, home.PendingProposals);
    }

    [Fact]
    public async Task Steps_are_ticked_off_as_they_are_done()
    {
        var dispatcher = new StubDispatcher
        {
            Coverage = [new MarketDataCoverage("SPY", default, [DateRange.Create(new DateOnly(2024, 1, 2), new DateOnly(2026, 10, 30))], DateTimeOffset.UnixEpoch)]
        };
        var home = Home(dispatcher, keys: true);

        await home.RefreshCommand.ExecuteAsync(null);

        Assert.Equal([true, true, false, false, false], home.Steps.Select(s => s.IsDone));
    }

    private static HomeViewModel Home(IDispatcher dispatcher, bool keys) =>
        new(dispatcher, new WeakReferenceMessenger(), new AppEnvironmentInfo("d", "r", "db", "l", keys, 3, "0.3.0"), new StatusViewModel(),
            NullLogger<HomeViewModel>.Instance);

    /// <summary>Answers the queries the home page asks; everything else fails, e.g. the account without a broker.</summary>
    private sealed class StubDispatcher : IDispatcher
    {
        public IReadOnlyList<MarketDataCoverage> Coverage { get; init; } = [];

        public Task<Result<TResponse>> SendAsync<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
        {
            object? response = request switch
            {
                GetMarketDataCoverageQuery => Coverage,
                GetBacktestHistoryQuery => Array.Empty<BacktestRunSummary>(),
                GetScansQuery => Array.Empty<ScanSummary>(),
                GetAutopilotQuery => new AutopilotView(AutopilotMode.Off, 3, 50m, null, null, 0m, []),
                GetProposalsQuery => Array.Empty<ProposalRow>(),
                _ => null
            };
            return Task.FromResult(response is TResponse typed
                ? Result<TResponse>.Success(typed)
                : (Result<TResponse>)Error.Failure("Test.NotAnswered", "Not answered in this test."));
        }
    }
}
