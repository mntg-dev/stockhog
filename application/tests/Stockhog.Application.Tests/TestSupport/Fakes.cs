using Stockhog.Application.Abstractions.Events;
using Stockhog.Application.Abstractions.Jobs;
using Stockhog.Application.Abstractions.Messaging;
using Stockhog.Application.Abstractions.Persistence;
using Stockhog.Application.Backtesting;
using Stockhog.Application.Backtesting.Strategies;
using Stockhog.Application.MarketData;
using Stockhog.Application.MarketData.GetPriceBars;
using Stockhog.Application.Optimization;
using Stockhog.Application.Trading;
using Stockhog.Application.Trading.Safety;
using Stockhog.Domain.Backtesting;
using Stockhog.Domain.Common;
using Stockhog.Domain.MarketData;
using Stockhog.Domain.Optimization;
using Stockhog.Domain.Trading;

namespace Stockhog.Application.Tests.TestSupport;

internal sealed class FakeBacktestEngine : IBacktestEngine
{
    public List<BacktestRequest> Requests { get; } = [];

    /// <summary>Runs inside RunAsync; throw from it to simulate engine failures.</summary>
    public Func<BacktestRequest, IProgress<BacktestProgress>?, CancellationToken, Task> Behavior { get; set; } =
        (_, progress, _) =>
        {
            progress?.Report(new BacktestProgress(50));
            progress?.Report(new BacktestProgress(100));
            return Task.CompletedTask;
        };

    public Task RunAsync(BacktestRequest request, IProgress<BacktestProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        lock (Requests)
            Requests.Add(request);
        return Behavior(request, progress, cancellationToken);
    }
}

internal sealed class CountingResultReader : IBacktestResultReader
{
    private int _readCalls;
    private int _latestCalls;

    public int ReadCalls => _readCalls;
    public int LatestCalls => _latestCalls;

    public BacktestReport? Latest { get; set; } = TestData.Report("latest");

    /// <summary>Report returned for a run id; <c>null</c> means the run has no stored result.</summary>
    public Func<string, BacktestReport?> ReportFor { get; set; } = TestData.Report;

    public Task<BacktestReport?> ReadAsync(Symbol symbol, BacktestRunId runId, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _readCalls);
        return Task.FromResult(ReportFor(runId.Value));
    }

    public Task<BacktestReport?> ReadLatestAsync(Symbol symbol, StrategyName strategy, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _latestCalls);
        return Task.FromResult(Latest);
    }
}

internal sealed class RecordingEventPublisher : IDomainEventPublisher
{
    public List<IDomainEvent> Published { get; } = [];

    public Task PublishAsync(IEnumerable<IDomainEvent> domainEvents, CancellationToken cancellationToken = default)
    {
        Published.AddRange(domainEvents);
        return Task.CompletedTask;
    }
}

/// <summary>
/// All repositories and readers over in-memory lists, shared across DI scopes like a real database would be.
/// Optimization trials use it from several threads, so every list access takes <see cref="Sync"/>.
/// </summary>
internal sealed class InMemoryDatabase :
    IBacktestRunRepository, IBacktestHistoryReader, IMarketDataSetRepository, IMarketDataCoverageReader,
    IOptimizationRunRepository, IOptimizationReader, IOrderRepository, IOrderReader
{
    public object Sync { get; } = new();

    public List<BacktestRun> Runs { get; } = [];

    public List<MarketDataSet> MarketDataSets { get; } = [];

    public List<OptimizationRun> Optimizations { get; } = [];

    public List<Order> Orders { get; } = [];

    /// <summary>Saves across every unit of work, i.e. every scope.</summary>
    public int SaveCount { get; set; }

    public IEnumerable<IHasDomainEvents> Aggregates => Runs.Cast<IHasDomainEvents>().Concat(MarketDataSets).Concat(Optimizations).Concat(Orders);

    public void Add(BacktestRun run)
    {
        lock (Sync)
            Runs.Add(run);
    }

    public Task<BacktestRun?> GetAsync(BacktestRunId id, CancellationToken cancellationToken = default)
    {
        lock (Sync)
            return Task.FromResult(Runs.FirstOrDefault(r => r.Id == id));
    }

    public Task<IReadOnlyList<BacktestRun>> ListUnfinishedAsync(CancellationToken cancellationToken = default)
    {
        lock (Sync)
            return Task.FromResult<IReadOnlyList<BacktestRun>>(Runs.Where(r => !r.IsFinished).ToList());
    }

    public Task<IReadOnlyList<BacktestRun>> ListCompletedWithoutMetricsAsync(int take, CancellationToken cancellationToken = default)
    {
        lock (Sync)
            return Task.FromResult<IReadOnlyList<BacktestRun>>(CompletedWithoutMetrics().Take(take).ToList());
    }

    public Task<int> CountCompletedWithoutMetricsAsync(CancellationToken cancellationToken = default)
    {
        lock (Sync)
            return Task.FromResult(CompletedWithoutMetrics().Count());
    }

    private IEnumerable<BacktestRun> CompletedWithoutMetrics() =>
        Runs.Where(r => r.Status == BacktestStatus.Completed && r.Metrics is null).OrderBy(r => r.CreatedAt);

    public Task<IReadOnlyList<RankedRun>> ListRankedAsync(CancellationToken cancellationToken = default)
    {
        lock (Sync)
        {
            return Task.FromResult<IReadOnlyList<RankedRun>>(Runs
                .Where(r => r.Status == BacktestStatus.Completed && r.OptimizationId is null && r.Metrics is not null)
                .OrderByDescending(r => r.CreatedAt)
                .Select(r => RankedRun.Create(r.Id.Value, r.Symbol.Value, r.Strategy.Value, r.Period.Start, r.Period.End,
                    r.InitialCapital.Amount, r.CreatedAt, r.Metrics!))
                .ToList());
        }
    }

    public Task<IReadOnlyList<BacktestRunSummary>> ListAsync(Symbol? symbol, int take, bool includeTrials = false, CancellationToken cancellationToken = default)
    {
        lock (Sync)
        {
            return Task.FromResult<IReadOnlyList<BacktestRunSummary>>(Runs
                .Where(r => symbol is null || r.Symbol == symbol)
                .Where(r => includeTrials || r.OptimizationId is null)
                .OrderByDescending(r => r.CreatedAt)
                .Take(take)
                .Select(r => new BacktestRunSummary(r.Id.Value, r.Symbol.Value, r.Strategy.Value, r.Period.Start, r.Period.End,
                    r.InitialCapital.Amount, r.Status, r.Progress, r.CreatedAt, r.Duration, r.FailureReason, r.OptimizationId?.Value))
                .ToList());
        }
    }

    public void Add(OptimizationRun run)
    {
        lock (Sync)
            Optimizations.Add(run);
    }

    public Task<OptimizationRun?> GetAsync(OptimizationRunId id, CancellationToken cancellationToken = default)
    {
        lock (Sync)
            return Task.FromResult(Optimizations.FirstOrDefault(r => r.Id == id));
    }

    Task<IReadOnlyList<OptimizationRun>> IOptimizationRunRepository.ListUnfinishedAsync(CancellationToken cancellationToken)
    {
        lock (Sync)
            return Task.FromResult<IReadOnlyList<OptimizationRun>>(Optimizations.Where(r => !r.IsFinished).ToList());
    }

    public Task<IReadOnlyList<OptimizationRun>> ListAsync(int take, CancellationToken cancellationToken = default)
    {
        lock (Sync)
            return Task.FromResult<IReadOnlyList<OptimizationRun>>(Optimizations.OrderByDescending(r => r.CreatedAt).Take(take).ToList());
    }

    public Task<OptimizationRun?> FindAsync(OptimizationRunId id, CancellationToken cancellationToken = default) => GetAsync(id, cancellationToken);

    public void Add(Order order)
    {
        lock (Sync)
            Orders.Add(order);
    }

    public Task<Order?> GetAsync(ClientOrderId id, CancellationToken cancellationToken = default)
    {
        lock (Sync)
            return Task.FromResult(Orders.FirstOrDefault(o => o.Id == id));
    }

    Task<IReadOnlyList<Order>> IOrderRepository.ListUnfinishedAsync(CancellationToken cancellationToken)
    {
        lock (Sync)
            return Task.FromResult<IReadOnlyList<Order>>(Orders.Where(o => !o.IsFinal).OrderBy(o => o.CreatedAt).ToList());
    }

    public Task<IReadOnlyList<OrderSummary>> ListAsync(bool openOnly, int take, CancellationToken cancellationToken = default)
    {
        lock (Sync)
        {
            return Task.FromResult<IReadOnlyList<OrderSummary>>(Orders
                .Where(o => !openOnly || !o.IsFinal)
                .OrderByDescending(o => o.CreatedAt)
                .Take(take)
                .Select(o => new OrderSummary(o.Id.Value, o.BrokerOrderId, o.Symbol.Value, o.Side, o.Type, o.Quantity, o.LimitPrice,
                    o.StopPrice, o.TimeInForce, o.Status, o.FilledQuantity, o.AverageFillPrice, o.RejectReason, o.CreatedAt, o.UpdatedAt))
                .ToList());
        }
    }

    public void Add(MarketDataSet set) => MarketDataSets.Add(set);

    public Task<MarketDataSet?> GetAsync(MarketDataSetId id, CancellationToken cancellationToken = default) =>
        Task.FromResult(MarketDataSets.FirstOrDefault(s => s.Id == id));

    public Task<IReadOnlyList<MarketDataCoverage>> ListAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<MarketDataCoverage>>(MarketDataSets
            .Select(s => new MarketDataCoverage(s.Symbol.Value, s.Timeframe, s.Coverage, s.LastDownloadedAt))
            .ToList());
}

/// <summary>
/// Mirrors the real unit of work: "commits" (records each run's status), then publishes the raised events.
/// </summary>
internal sealed class InMemoryUnitOfWork(InMemoryDatabase database, IDomainEventPublisher publisher) : IUnitOfWork
{
    public List<BacktestStatus> SavedStatuses { get; } = [];

    public int SaveCount { get; private set; }

    public async Task SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        List<IDomainEvent> events;
        lock (database.Sync)
        {
            SaveCount++;
            database.SaveCount++;
            SavedStatuses.AddRange(database.Runs.Select(r => r.Status));
            events = database.Aggregates.SelectMany(a => a.DequeueDomainEvents()).ToList();
        }

        await publisher.PublishAsync(events, cancellationToken);
    }
}

/// <summary>Records requests and returns a configurable number of bars per downloaded range.</summary>
internal sealed class FakeMarketDataDownloader : IMarketDataDownloader
{
    public List<MarketDataRequest> Requests { get; } = [];

    public Func<MarketDataRequest, int> BarsFor { get; set; } = request => request.Period.Days * 10;

    public Task<MarketDataDownloadResult> DownloadAsync(MarketDataRequest request, IProgress<int>? progress = null, CancellationToken cancellationToken = default)
    {
        Requests.Add(request);
        progress?.Report(50);
        progress?.Report(100);
        var bars = BarsFor(request);
        return Task.FromResult(new MarketDataDownloadResult(request.Symbol.Value, bars, bars > 0 ? 1 : 0));
    }
}

/// <summary>
/// Collects jobs instead of running them in the background; <see cref="RunAllAsync"/> runs them inline.
/// </summary>
internal sealed class InlineJobQueue : IJobQueue
{
    public List<(Guid Id, string Title, string? Key, JobWork Work)> Pending { get; } = [];

    public JobEnqueueResult Enqueue(string title, JobWork work, string? key = null)
    {
        var existing = Pending.FirstOrDefault(p => key is not null && p.Key == key);
        if (existing.Work is not null)
            return new JobEnqueueResult(Snapshot(existing.Id, existing.Title, existing.Key), WasQueued: false);

        var id = Guid.NewGuid();
        Pending.Add((id, title, key, work));
        return new JobEnqueueResult(Snapshot(id, title, key), WasQueued: true);
    }

    public bool TryCancel(Guid jobId) => Pending.RemoveAll(p => p.Id == jobId) > 0;

    public void ClearFinished()
    {
    }

    public async Task<List<JobOutcome>> RunAllAsync(IDispatcher dispatcher)
    {
        var outcomes = new List<JobOutcome>();
        foreach (var job in Pending.ToList())
            outcomes.Add(await job.Work(new JobExecutionContext(dispatcher, new SyncProgress<JobProgress>(_ => { }), CancellationToken.None)));

        Pending.Clear();
        return outcomes;
    }

    private static JobSnapshot Snapshot(Guid id, string title, string? key) =>
        new(id, title, key, JobStatus.Queued, 0, null, TestData.Now, null, null);
}

/// <summary>
/// A catalog with one strategy shaped like the real SmaCrossStrategy: two integer periods, slow must exceed fast.
/// </summary>
internal sealed class FakeStrategyCatalog : IStrategyCatalog
{
    public const string FastPeriod = "fast-period";
    public const string SlowPeriod = "slow-period";

    public static readonly StrategyDescriptor SmaCross = new("SmaCrossStrategy", "EMA Cross", null,
    [
        new StrategyParameterDescriptor(FastPeriod, "Fast EMA period", StrategyParameterKind.Integer, 26, 2, 500, null, null),
        new StrategyParameterDescriptor(SlowPeriod, "Slow EMA period", StrategyParameterKind.Integer, 130, 3, 2000, FastPeriod, null),
        new StrategyParameterDescriptor("risk-percent", "Risk per trade (%)", StrategyParameterKind.Decimal, 1, 0.1m, 10, null, null)
    ]);

    public IReadOnlyList<StrategyDescriptor> GetAll() => [SmaCross];

    public StrategyDescriptor? Find(string name) => GetAll().FirstOrDefault(s => s.Name == name);
}

/// <summary>Returns one bar per day of the period and counts reads.</summary>
internal sealed class FakePriceBarReader : IPriceBarReader
{
    private int _reads;

    public int Reads => _reads;

    public Task<IReadOnlyList<PriceBarData>> ReadAsync(Symbol symbol, BarResolution resolution, DateRange period, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _reads);
        IReadOnlyList<PriceBarData> bars = Enumerable.Range(0, period.Days)
            .Select(i => new PriceBarData(period.Start.AddDays(i).ToDateTime(TimeOnly.MinValue), 10, 11, 9, 10.5m, 1000))
            .ToList();
        return Task.FromResult(bars);
    }
}

/// <summary>
/// An in-memory broker: accepts orders (optionally filling market orders at once), keeps positions, and records
/// every call. Throw from <see cref="SubmitBehavior"/> to simulate rejections and timeouts.
/// </summary>
internal sealed class FakeBrokerGateway : IBrokerGateway
{
    private int _nextId;

    public BrokerAccount Account { get; set; } = new("PA123", "USD", 100_000, 100_000, 50_000, 200_000, false, true);

    public List<BrokerPosition> Positions { get; } = [];

    /// <summary>Broker-side orders by client order id.</summary>
    public Dictionary<string, BrokerOrder> Orders { get; } = [];

    public List<string> Cancelled { get; } = [];

    public bool FillMarketOrdersImmediately { get; set; }

    public decimal FillPrice { get; set; } = 100;

    public Func<Order, BrokerOrder?>? SubmitBehavior { get; set; }

    public Task<BrokerAccount> GetAccountAsync(CancellationToken cancellationToken = default) => Task.FromResult(Account);

    public Task<IReadOnlyList<BrokerPosition>> GetPositionsAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<BrokerPosition>>(Positions.ToList());

    public Task<BrokerOrder?> GetOrderAsync(ClientOrderId clientOrderId, CancellationToken cancellationToken = default)
    {
        lock (Orders)
            return Task.FromResult(Orders.GetValueOrDefault(clientOrderId.Value));
    }

    public Task<BrokerOrder> SubmitOrderAsync(Order order, CancellationToken cancellationToken = default)
    {
        var reported = SubmitBehavior?.Invoke(order);
        reported ??= FillMarketOrdersImmediately && order.Type == OrderType.Market
            ? Report(order, OrderStatus.Filled, order.Quantity, FillPrice)
            : Report(order, OrderStatus.Submitted, 0, null);

        lock (Orders)
            Orders[order.Id.Value] = reported;
        return Task.FromResult(reported);
    }

    public Task CancelOrderAsync(string brokerOrderId, CancellationToken cancellationToken = default)
    {
        lock (Orders)
        {
            Cancelled.Add(brokerOrderId);
            var (key, existing) = Orders.First(o => o.Value.BrokerOrderId == brokerOrderId);
            if (existing.Status is OrderStatus.Filled or OrderStatus.Cancelled)
                throw new BrokerException("order is not cancelable", isRejection: true);
            Orders[key] = existing with { Status = OrderStatus.Cancelled };
        }

        return Task.CompletedTask;
    }

    public BrokerOrder Report(Order order, OrderStatus status, decimal filled, decimal? price) =>
        new($"broker-{Interlocked.Increment(ref _nextId)}", order.Id.Value, order.Symbol.Value, status, order.Quantity, filled, price, TestData.Now);
}

internal sealed class FakeQuoteCache : IQuoteCache
{
    public Dictionary<string, Quote> Quotes { get; } = [];

    public bool TryGet(Symbol symbol, out Quote quote) => Quotes.TryGetValue(symbol.Value, out quote!);

    public IReadOnlyList<Quote> Snapshot() => Quotes.Values.ToList();
}

internal sealed class FakeLatestPriceSource : ILatestPriceSource
{
    public decimal? Price { get; set; } = 100;

    public Task<decimal?> GetLatestPriceAsync(Symbol symbol, CancellationToken cancellationToken = default) => Task.FromResult(Price);
}

internal sealed class FakeRiskLimitsStore : IRiskLimitsStore
{
    public RiskLimits Current { get; private set; } = RiskLimits.Default;

    public void Update(RiskLimits limits) => Current = limits;
}

internal sealed class FakeTradingEnvironment : ITradingEnvironment
{
    public TradingMode Mode { get; set; } = TradingMode.Paper;
}

internal sealed class InMemoryAuditLog : IAuditLog
{
    private readonly List<AuditEntry> _entries = [];

    public IReadOnlyList<AuditEntry> Entries
    {
        get
        {
            lock (_entries)
                return _entries.ToList();
        }
    }

    public Task RecordAsync(AuditEntry entry, CancellationToken cancellationToken = default)
    {
        lock (_entries)
            _entries.Add(entry);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<AuditEntry>> ListAsync(int take, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<AuditEntry>>(Entries.AsEnumerable().Reverse().Take(take).ToList());
}

internal sealed class SyncProgress<T>(Action<T> report) : IProgress<T>
{
    public void Report(T value) => report(value);
}

internal static class TestData
{
    public static readonly DateTimeOffset Now = new(2026, 10, 15, 12, 0, 0, TimeSpan.Zero);

    public static BacktestReport Report(string runId) =>
        new(runId, new Dictionary<string, string>(), new TradeStatistics(0, 0, 0, 0), [], []);
}
