using System.Collections.Concurrent;
using Stockhog.Application.Autopilot;
using Stockhog.Application.Brokers;
using Stockhog.Application.Common;
using Stockhog.Application.Proposals;
using Stockhog.Application.Scanning;
using Stockhog.Application.Screening;
using Stockhog.Application.Notifications;
using Stockhog.Application.Signals;
using Stockhog.Application.Updates;
using Stockhog.Application.Trading.Portfolio;
using Stockhog.Domain.Autopilot;
using Stockhog.Domain.Backtesting;
using Stockhog.Domain.MarketData;
using Stockhog.Domain.Proposals;
using Stockhog.Domain.Scanning;

namespace Stockhog.Application.Tests.TestSupport;

internal sealed class FakeDailyBarSource : IDailyBarSource
{
    public bool IsConfigured { get; set; } = true;

    public int MaxSymbolsPerRequest { get; set; } = 200;

    public Dictionary<string, IReadOnlyList<DailyBar>> Bars { get; } = new(StringComparer.Ordinal);

    /// <summary>Requests as (symbols, start, end); the screener sends batches in parallel.</summary>
    public ConcurrentQueue<(IReadOnlyList<string> Symbols, DateOnly Start, DateOnly End)> Requests { get; } = new();

    public Task<IReadOnlyDictionary<string, IReadOnlyList<DailyBar>>> GetDailyBarsAsync(
        IReadOnlyList<string> symbols, DateOnly start, DateOnly end, CancellationToken cancellationToken = default)
    {
        Requests.Enqueue((symbols.ToList(), start, end));
        IReadOnlyDictionary<string, IReadOnlyList<DailyBar>> result = symbols
            .Where(Bars.ContainsKey)
            .ToDictionary(s => s, s => Bars[s], StringComparer.Ordinal);
        return Task.FromResult(result);
    }

    /// <summary>
    /// <paramref name="count"/> weekday bars ending on <paramref name="lastDate"/>, the close moving by
    /// <paramref name="dailyChange"/> each day, with a fixed high-low range of 2% of the close.
    /// </summary>
    public static IReadOnlyList<DailyBar> Series(int count, DateOnly lastDate, decimal firstClose, decimal dailyChange = 0, long volume = 1_000_000)
    {
        var dates = new List<DateOnly>();
        for (var date = lastDate; dates.Count < count; date = date.AddDays(-1))
        {
            if (date.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday))
                dates.Add(date);
        }

        dates.Reverse();
        return dates.Select((date, i) =>
        {
            var close = firstClose + dailyChange * i;
            return new DailyBar(date, close, close * 1.01m, close * 0.99m, close, volume);
        }).ToList();
    }
}

internal sealed class InMemoryScreenerResultStore : IScreenerResultStore
{
    public List<ScreenerResult> Results { get; } = [];

    public Task SaveAsync(ScreenerResult result, CancellationToken cancellationToken = default)
    {
        Results.Add(result);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<ScreenerRunSummary>> ListRunsAsync(int take, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<ScreenerRunSummary>>(Results.Select(r => r.Summary).OrderByDescending(s => s.CreatedAt).Take(take).ToList());

    public Task<ScreenerResult?> GetAsync(Guid? id, CancellationToken cancellationToken = default) =>
        Task.FromResult(id is null
            ? Results.OrderByDescending(r => r.Summary.CreatedAt).FirstOrDefault()
            : Results.FirstOrDefault(r => r.Summary.Id == id));
}

internal sealed class InMemoryScanStore : IScanRunRepository, IScanReader
{
    private readonly object _sync = new();

    public List<ScanRun> Scans { get; } = [];

    public void Add(ScanRun run)
    {
        lock (_sync)
            Scans.Add(run);
    }

    public Task<ScanRun?> GetAsync(ScanRunId id, CancellationToken cancellationToken = default)
    {
        lock (_sync)
            return Task.FromResult(Scans.FirstOrDefault(s => s.Id == id));
    }

    public Task<IReadOnlyList<ScanRun>> ListUnfinishedAsync(CancellationToken cancellationToken = default)
    {
        lock (_sync)
            return Task.FromResult<IReadOnlyList<ScanRun>>(Scans.Where(s => !s.IsFinished).ToList());
    }

    public Task<IReadOnlyList<ScanRun>> ListAsync(int take, CancellationToken cancellationToken = default)
    {
        lock (_sync)
            return Task.FromResult<IReadOnlyList<ScanRun>>(Scans.OrderByDescending(s => s.CreatedAt).Take(take).ToList());
    }

    public Task<ScanRun?> FindAsync(ScanRunId id, CancellationToken cancellationToken = default) => GetAsync(id, cancellationToken);
}

internal sealed class FakeStrategyStateReader : IStrategyStateReader
{
    /// <summary>State returned for a run id; <c>null</c> means the run stored none.</summary>
    public Func<string, StrategyEndState?> StateFor { get; set; } = _ => null;

    public Task<StrategyEndState?> ReadAsync(Symbol symbol, BacktestRunId runId, CancellationToken cancellationToken = default) =>
        Task.FromResult(StateFor(runId.Value));
}

internal sealed class InMemoryProposalStore : IProposalRepository, IProposalReader
{
    public List<TradeProposal> Proposals { get; } = [];

    public void Add(TradeProposal proposal) => Proposals.Add(proposal);

    public Task<TradeProposal?> GetAsync(ProposalId id, CancellationToken cancellationToken = default) =>
        Task.FromResult(Proposals.FirstOrDefault(p => p.Id == id));

    public Task<IReadOnlyList<TradeProposal>> ListPendingAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<TradeProposal>>(Proposals.Where(p => p.IsPending).ToList());

    public Task<IReadOnlyList<TradeProposal>> ListAsync(int take, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<TradeProposal>>(Proposals.OrderByDescending(p => p.CreatedAt).Take(take).ToList());
}

internal sealed class InMemoryEquityPeakStore : IEquityPeakStore
{
    public Dictionary<string, EquityPeak> Peaks { get; } = [];

    public Task<EquityPeak?> GetAsync(string accountNumber, CancellationToken cancellationToken = default)
    {
        lock (Peaks)
            return Task.FromResult(Peaks.GetValueOrDefault(accountNumber));
    }

    public Task SaveAsync(EquityPeak peak, CancellationToken cancellationToken = default)
    {
        lock (Peaks)
        {
            if (!Peaks.TryGetValue(peak.AccountNumber, out var current) || peak.Equity > current.Equity)
                Peaks[peak.AccountNumber] = peak;
        }

        return Task.CompletedTask;
    }
}

internal sealed class InMemoryAutopilotStore : IAutopilotRepository, IAutopilotDecisionLog
{
    public Domain.Autopilot.Autopilot Autopilot { get; } = Domain.Autopilot.Autopilot.Create();

    public List<AutopilotDecision> Decisions { get; } = [];

    public Task<Domain.Autopilot.Autopilot> GetOrCreateAsync(CancellationToken cancellationToken = default) => Task.FromResult(Autopilot);

    public Task AppendAsync(IReadOnlyList<AutopilotDecision> decisions, CancellationToken cancellationToken = default)
    {
        lock (Decisions)
            Decisions.AddRange(decisions);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<AutopilotDecision>> ListAsync(int take, CancellationToken cancellationToken = default)
    {
        lock (Decisions)
            return Task.FromResult<IReadOnlyList<AutopilotDecision>>(Decisions.AsEnumerable().Reverse().Take(take).ToList());
    }
}

internal sealed class FakeBrokerKeyStore : IBrokerKeyStore
{
    public BrokerKeySource SourceInUse { get; set; } = BrokerKeySource.None;

    public string? KeyInUse { get; set; }

    public BrokerKeys? Stored { get; set; }

    public void Save(BrokerKeys? keys) => Stored = keys;
}

internal sealed class FakeBrokerKeyTester : IBrokerKeyTester
{
    public BrokerKeyCheck Result { get; set; } = new(null, null, "PA123");

    public List<BrokerKeys> Tested { get; } = [];

    public Task<BrokerKeyCheck> TestAsync(BrokerKeys keys, CancellationToken cancellationToken = default)
    {
        Tested.Add(keys);
        return Task.FromResult(Result);
    }
}

internal sealed class FakeMarketCalendar : IMarketCalendar
{
    /// <summary>Null: the calendar cannot be read, so weekdays with regular hours are assumed.</summary>
    public List<MarketSession>? Sessions { get; set; }

    public Task<IReadOnlyList<MarketSession>?> GetSessionsAsync(DateOnly from, DateOnly to, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<MarketSession>?>(Sessions?.Where(s => s.Date >= from && s.Date <= to).ToList());
}

internal sealed class FakeReleaseFeed : IReleaseFeed
{
    public ReleaseInfo? Latest { get; set; }

    public Task<ReleaseInfo?> GetLatestAsync(CancellationToken cancellationToken = default) => Task.FromResult(Latest);
}

internal sealed class RecordingNotifier : INotifier
{
    public List<Notice> Notices { get; } = [];

    public void Notify(Notice notice)
    {
        lock (Notices)
            Notices.Add(notice);
    }
}
