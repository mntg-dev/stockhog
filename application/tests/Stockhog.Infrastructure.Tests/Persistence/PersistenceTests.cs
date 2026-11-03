using Microsoft.Extensions.DependencyInjection;
using Stockhog.Application.Abstractions.Persistence;
using Stockhog.Application.Trading.Safety;
using Stockhog.Application.Abstractions.Events;
using Stockhog.Application.Instruments;
using Stockhog.Application.Screening;
using Stockhog.Domain.Backtesting;
using Stockhog.Domain.Common;
using Stockhog.Domain.MarketData;
using Stockhog.Domain.Optimization;
using Stockhog.Domain.Proposals;
using Stockhog.Domain.Scanning;
using Stockhog.Domain.Signals;
using Stockhog.Domain.Trading;
using Stockhog.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Stockhog.Infrastructure.Tests.Persistence;

/// <summary>
/// Real SQLite (in memory) with the real migrations, so mapping, conversions and SQL translation are all exercised.
/// </summary>
public sealed class PersistenceTests : IAsyncLifetime
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 15, 12, 0, 0, TimeSpan.Zero);

    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly RecordingPublisher _publisher = new();

    public async ValueTask InitializeAsync()
    {
        await _connection.OpenAsync();
        await using var db = CreateContext();
        await db.Database.MigrateAsync();
    }

    public async ValueTask DisposeAsync() => await _connection.DisposeAsync();

    [Fact]
    public async Task Migrations_match_the_current_model()
    {
        await using var db = CreateContext();

        Assert.False(db.Database.HasPendingModelChanges(), "The model changed; add a migration with dotnet ef migrations add.");
    }

    [Fact]
    public async Task Backtest_run_round_trips_with_all_value_objects()
    {
        var ct = TestContext.Current.CancellationToken;
        var run = CreateRun("AAPL", T0);
        run.Start(T0.AddSeconds(1));
        run.ReportProgress(40);
        run.Cancel(T0.AddSeconds(31));

        await using (var db = CreateContext())
        {
            new BacktestRunRepository(db).Add(run);
            await new UnitOfWork(db, _publisher).SaveChangesAsync(ct);
        }

        await using (var db = CreateContext())
        {
            var loaded = await new BacktestRunRepository(db).GetAsync(run.Id, ct);

            Assert.NotNull(loaded);
            Assert.Equal(run.Symbol, loaded.Symbol);
            Assert.Equal(run.Strategy, loaded.Strategy);
            Assert.Equal(run.Period, loaded.Period);
            Assert.Equal(run.InitialCapital, loaded.InitialCapital);
            Assert.Equal(run.Parameters, loaded.Parameters);
            Assert.Equal(BacktestStatus.Cancelled, loaded.Status);
            Assert.Equal(40, loaded.Progress);
            Assert.Equal(TimeSpan.FromSeconds(30), loaded.Duration);
            Assert.Equal(TimeSpan.Zero, loaded.CreatedAt.Offset);
            Assert.Null(loaded.Metrics);
            Assert.Empty(loaded.DomainEvents);
        }
    }

    [Fact]
    public async Task Completed_run_metrics_round_trip_and_can_be_added_later()
    {
        var ct = TestContext.Current.CancellationToken;
        var metrics = new PerformanceMetrics(12.5m, 4.25m, 1.3m, 17, 52.94m, 1.8m);
        var withMetrics = CreateRun("AAPL", T0);
        withMetrics.Start(T0);
        withMetrics.Complete(T0.AddSeconds(5), metrics);
        var olderRun = CreateRun("MSFT", T0);
        olderRun.Start(T0);
        olderRun.Complete(T0.AddSeconds(5));
        await SaveAsync(withMetrics);
        await SaveAsync(olderRun);

        await using (var db = CreateContext())
        {
            var pending = new BacktestRunRepository(db);
            Assert.Equal(1, await pending.CountCompletedWithoutMetricsAsync(ct));
            var loaded = Assert.Single(await pending.ListCompletedWithoutMetricsAsync(10, ct));
            Assert.Equal(olderRun.Id, loaded.Id);

            loaded.RecordMetrics(metrics with { NetProfitPercent = -3m });
            await new UnitOfWork(db, _publisher).SaveChangesAsync(ct);
        }

        await using var check = CreateContext();
        var repository = new BacktestRunRepository(check);
        Assert.Equal(0, await repository.CountCompletedWithoutMetricsAsync(ct));
        Assert.Equal(metrics, (await repository.GetAsync(withMetrics.Id, ct))!.Metrics);
        Assert.Equal(-3m, (await repository.GetAsync(olderRun.Id, ct))!.Metrics!.NetProfitPercent);

        var ranked = await new BacktestHistoryReader(check).ListRankedAsync(ct);
        Assert.Equal([("AAPL", 12.5m), ("MSFT", -3m)], ranked.OrderBy(r => r.Symbol).Select(r => (r.Symbol, r.NetProfitPercent)));
        Assert.Equal(new DateOnly(2026, 1, 2), ranked[0].Start);
    }

    [Fact]
    public async Task Unit_of_work_publishes_events_only_after_saving()
    {
        var ct = TestContext.Current.CancellationToken;
        var run = CreateRun("AAPL", T0);
        run.Start(T0);
        run.Complete(T0.AddSeconds(5));

        await using var db = CreateContext();
        _publisher.OnPublish = () =>
        {
            // The handler must already see the committed row.
            using var other = CreateContext();
            Assert.Equal(BacktestStatus.Completed, other.BacktestRuns.AsNoTracking().Single().Status);
        };

        new BacktestRunRepository(db).Add(run);
        await new UnitOfWork(db, _publisher).SaveChangesAsync(ct);

        Assert.IsType<BacktestRunCompleted>(Assert.Single(_publisher.Published));
        Assert.Empty(run.DomainEvents);
    }

    [Fact]
    public async Task Changes_to_a_loaded_run_are_tracked_and_saved()
    {
        var ct = TestContext.Current.CancellationToken;
        var run = CreateRun("MSFT", T0);
        run.Start(T0);
        await SaveAsync(run);

        await using (var db = CreateContext())
        {
            var repository = new BacktestRunRepository(db);
            var unfinished = Assert.Single(await repository.ListUnfinishedAsync(ct));
            unfinished.Fail("Interrupted", T0.AddMinutes(1));
            await new UnitOfWork(db, _publisher).SaveChangesAsync(ct);
        }

        await using (var db = CreateContext())
        {
            Assert.Empty(await new BacktestRunRepository(db).ListUnfinishedAsync(ct));
            Assert.Equal("Interrupted", (await new BacktestRunRepository(db).GetAsync(run.Id, ct))!.FailureReason);
        }
    }

    [Fact]
    public async Task History_is_filtered_by_symbol_newest_first()
    {
        var ct = TestContext.Current.CancellationToken;
        var older = CreateRun("AAPL", T0);
        older.Start(T0);
        older.Complete(T0.AddSeconds(12));
        await SaveAsync(older);
        await SaveAsync(CreateRun("MSFT", T0.AddMinutes(1)));
        var newer = CreateRun("AAPL", T0.AddMinutes(2));
        await SaveAsync(newer);

        await using var db = CreateContext();
        var reader = new BacktestHistoryReader(db);

        var aapl = await reader.ListAsync(Symbol.Create("AAPL"), take: 10, cancellationToken: ct);
        Assert.Equal([newer.Id.Value, older.Id.Value], aapl.Select(r => r.RunId));
        Assert.Equal(TimeSpan.FromSeconds(12), aapl[1].Duration);
        Assert.Equal(100_000m, aapl[1].InitialCapital);
        Assert.True(aapl[1].HasReport);

        Assert.Equal(2, (await reader.ListAsync(null, take: 2, cancellationToken: ct)).Count);
    }

    [Fact]
    public async Task Market_data_set_coverage_round_trips_and_changes_are_detected()
    {
        var ct = TestContext.Current.CancellationToken;
        var completeThrough = new DateOnly(2026, 3, 31);
        var set = MarketDataSet.Create(Symbol.Create("MSFT"), Timeframe.Daily);
        set.RecordDownload(DateRange.Create(new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31)), completeThrough, 21, T0);

        await using (var db = CreateContext())
        {
            new MarketDataSetRepository(db).Add(set);
            await new UnitOfWork(db, _publisher).SaveChangesAsync(ct);
        }

        // Load, extend the coverage (the aggregate replaces its list), save again.
        await using (var db = CreateContext())
        {
            var loaded = await new MarketDataSetRepository(db).GetAsync(set.Id, ct);
            Assert.NotNull(loaded);
            loaded.RecordDownload(DateRange.Create(new DateOnly(2026, 3, 1), new DateOnly(2026, 3, 10)), completeThrough, 7, T0.AddDays(1));
            await new UnitOfWork(db, _publisher).SaveChangesAsync(ct);
        }

        await using (var db = CreateContext())
        {
            var coverage = Assert.Single(await new MarketDataCoverageReader(db).ListAsync(ct));
            Assert.Equal("MSFT", coverage.Symbol);
            Assert.Equal(Timeframe.Daily, coverage.Timeframe);
            Assert.Equal("2026-01-01..2026-01-31; 2026-03-01..2026-03-10", coverage.RangesText);
            Assert.Equal(T0.AddDays(1), coverage.LastDownloadedAt);
        }

        Assert.Equal(2, _publisher.Published.OfType<MarketDataDownloaded>().Count());
    }

    [Fact]
    public async Task Optimization_round_trips_with_grid_trials_and_metrics()
    {
        var ct = TestContext.Current.CancellationToken;
        var optimization = CreateOptimization(T0);
        optimization.Start(T0);
        optimization.StartTrial(0, BacktestRunId.ForTrial(optimization.Id, 0));
        optimization.CompleteTrial(0, new PerformanceMetrics(12.5m, 4.25m, 1.3m, 17, 52.94m, 1.8m), T0.AddMinutes(1));

        await using (var db = CreateContext())
        {
            new OptimizationRunRepository(db).Add(optimization);
            await new UnitOfWork(db, _publisher).SaveChangesAsync(ct);
        }

        // Load for changes, finish the remaining trial: only the changed rows need saving.
        await using (var db = CreateContext())
        {
            var loaded = await new OptimizationRunRepository(db).GetAsync(optimization.Id, ct);
            Assert.NotNull(loaded);
            loaded.StartTrial(1, BacktestRunId.ForTrial(loaded.Id, 1));
            loaded.FailTrial(1, "no data", T0.AddMinutes(2));
            await new UnitOfWork(db, _publisher).SaveChangesAsync(ct);
        }

        await using (var db = CreateContext())
        {
            var loaded = await new OptimizationReader(db).FindAsync(optimization.Id, ct);

            Assert.NotNull(loaded);
            Assert.Equal(optimization.Grid, loaded.Grid);
            Assert.Equal(OptimizationObjective.ReturnOverDrawdown, loaded.Objective);
            Assert.Equal(OptimizationStatus.Completed, loaded.Status);
            Assert.Equal(0, loaded.BestTrialIndex);
            Assert.Equal([TrialStatus.Completed, TrialStatus.Failed], loaded.Trials.Select(t => t.Status));
            Assert.Equal(new PerformanceMetrics(12.5m, 4.25m, 1.3m, 17, 52.94m, 1.8m), loaded.Trials[0].Metrics);
            Assert.Equal(optimization.Trials[1].Parameters, loaded.Trials[1].Parameters);
            Assert.Equal($"{optimization.Id.Value}-t001", loaded.Trials[1].RunId!.Value);
            Assert.Equal("no data", loaded.Trials[1].FailureReason);
            Assert.Null(loaded.Trials[1].Metrics);
        }

        Assert.IsType<OptimizationRunCompleted>(Assert.Single(_publisher.Published));
    }

    [Fact]
    public async Task Optimization_check_round_trips_its_period_and_trial_results()
    {
        var ct = TestContext.Current.CancellationToken;
        var optimization = CreateOptimization(T0);
        optimization.Start(T0);
        foreach (var index in new[] { 0, 1 })
            optimization.StartTrial(index, BacktestRunId.ForTrial(optimization.Id, index));
        optimization.CompleteTrial(0, new PerformanceMetrics(12.5m, 4.25m, 1.3m, 17, 52.94m, 1.8m), T0);
        optimization.CompleteTrial(1, new PerformanceMetrics(2m, 3m, 0.4m, 9, 44m, 1.1m), T0);

        await using (var db = CreateContext())
        {
            new OptimizationRunRepository(db).Add(optimization);
            await new UnitOfWork(db, _publisher).SaveChangesAsync(ct);
        }

        var checkRun = BacktestRunId.From("20261015-120000000_AAPL_SmaCrossStrategy");
        var period = DateRange.Create(new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 14));
        await using (var db = CreateContext())
        {
            var loaded = (await new OptimizationRunRepository(db).GetAsync(optimization.Id, ct))!;
            loaded.StartCheck(period, 2);
            loaded.StartTrialCheck(0);
            loaded.CompleteTrialCheck(0, checkRun, new PerformanceMetrics(-1m, 6m, -0.2m, 5, 40m, 0.8m));
            loaded.StartTrialCheck(1);
            loaded.FailTrialCheck(1, "no data");
            await new UnitOfWork(db, _publisher).SaveChangesAsync(ct);
        }

        await using (var db = CreateContext())
        {
            var loaded = (await new OptimizationReader(db).FindAsync(optimization.Id, ct))!;

            Assert.Equal(period, loaded.CheckPeriod);
            Assert.Equal([TrialStatus.Completed, TrialStatus.Failed], loaded.Trials.Select(t => t.CheckStatus!.Value));
            Assert.Equal(checkRun, loaded.Trials[0].CheckRunId);
            Assert.Equal(new PerformanceMetrics(-1m, 6m, -0.2m, 5, 40m, 0.8m), loaded.Trials[0].CheckMetrics);
            Assert.Equal("no data", loaded.Trials[1].CheckFailureReason);
        }
    }

    [Fact]
    public async Task Unfinished_optimizations_are_listed_and_the_newest_comes_first()
    {
        var ct = TestContext.Current.CancellationToken;
        var older = CreateOptimization(T0);
        var newer = CreateOptimization(T0.AddMinutes(5));
        newer.Cancel(T0.AddMinutes(6));

        await using (var db = CreateContext())
        {
            var repository = new OptimizationRunRepository(db);
            repository.Add(older);
            repository.Add(newer);
            await new UnitOfWork(db, _publisher).SaveChangesAsync(ct);
        }

        await using (var db = CreateContext())
        {
            Assert.Equal([older.Id], (await new OptimizationRunRepository(db).ListUnfinishedAsync(ct)).Select(r => r.Id));
            Assert.Equal([newer.Id, older.Id], (await new OptimizationReader(db).ListAsync(10, ct)).Select(r => r.Id));
        }
    }

    [Fact]
    public async Task Trial_runs_are_linked_to_their_optimization_and_left_out_of_the_history()
    {
        var ct = TestContext.Current.CancellationToken;
        var optimizationId = OptimizationRunId.New(Symbol.Create("AAPL"), StrategyName.Create("SmaCrossStrategy"), T0);
        await SaveAsync(CreateRun("AAPL", T0));
        await SaveAsync(CreateRun("AAPL", T0.AddMinutes(1), optimizationId));

        await using var db = CreateContext();
        var reader = new BacktestHistoryReader(db);

        Assert.Null(Assert.Single(await reader.ListAsync(null, 10, cancellationToken: ct)).OptimizationId);
        var all = await reader.ListAsync(null, 10, includeTrials: true, ct);
        Assert.Equal(optimizationId.Value, all[0].OptimizationId);
    }

    [Fact]
    public async Task Orders_round_trip_and_unfinished_ones_are_listed_oldest_first()
    {
        var ct = TestContext.Current.CancellationToken;
        var aapl = Symbol.Create("AAPL");
        var filled = Order.Create(ClientOrderId.New(T0), aapl, OrderSide.Buy, OrderType.Market, 2.5m, null, null, TimeInForce.Day, T0);
        filled.MarkSubmitted("b-1", T0);
        filled.ApplyFill(2.5m, 187.123456m, T0.AddSeconds(1));
        var working = Order.Create(ClientOrderId.New(T0.AddMinutes(1)), aapl, OrderSide.Sell, OrderType.StopLimit, 3, 180, 181, TimeInForce.Gtc, T0.AddMinutes(1));
        working.MarkSubmitted("b-2", T0.AddMinutes(1));
        var fresh = Order.Create(ClientOrderId.New(T0.AddMinutes(2)), aapl, OrderSide.Buy, OrderType.Limit, 1, 150, null, TimeInForce.Day, T0.AddMinutes(2));

        await using (var db = CreateContext())
        {
            var repository = new OrderRepository(db);
            repository.Add(fresh);
            repository.Add(filled);
            repository.Add(working);
            await new UnitOfWork(db, _publisher).SaveChangesAsync(ct);
        }

        await using (var db = CreateContext())
        {
            var repository = new OrderRepository(db);
            Assert.Equal([working.Id, fresh.Id], (await repository.ListUnfinishedAsync(ct)).Select(o => o.Id));

            var loaded = await repository.GetAsync(filled.Id, ct);
            Assert.NotNull(loaded);
            Assert.Equal((OrderStatus.Filled, 2.5m, 187.123456m, "b-1"), (loaded.Status, loaded.FilledQuantity, loaded.AverageFillPrice, loaded.BrokerOrderId));

            var open = await new OrderReader(db).ListAsync(openOnly: true, take: 10, ct);
            Assert.Equal([fresh.Id.Value, working.Id.Value], open.Select(o => o.ClientOrderId));
            Assert.Equal((OrderType.StopLimit, 180m, 181m, TimeInForce.Gtc), (open[1].Type, open[1].LimitPrice, open[1].StopPrice, open[1].TimeInForce));
        }

        Assert.Contains(_publisher.Published, e => e is OrderFilled);
    }

    [Fact]
    public async Task A_save_based_on_an_outdated_order_is_refused_instead_of_overwriting_a_fill()
    {
        var ct = TestContext.Current.CancellationToken;
        var order = Order.Create(ClientOrderId.New(T0), Symbol.Create("AAPL"), OrderSide.Buy, OrderType.Market, 10, null, null, TimeInForce.Day, T0);
        await using (var db = CreateContext())
        {
            new OrderRepository(db).Add(order);
            await new UnitOfWork(db, _publisher).SaveChangesAsync(ct);
        }

        // Two scopes load the same new order: the submitting handler and the trade update stream.
        await using var handlerDb = CreateContext();
        await using var streamDb = CreateContext();
        var handlerCopy = (await new OrderRepository(handlerDb).GetAsync(order.Id, ct))!;
        var streamCopy = (await new OrderRepository(streamDb).GetAsync(order.Id, ct))!;

        streamCopy.MarkSubmitted("b-1", T0);
        streamCopy.ApplyFill(10, 101, T0.AddSeconds(1));
        await new UnitOfWork(streamDb, _publisher).SaveChangesAsync(ct);

        handlerCopy.MarkSubmitted("b-1", T0);
        var conflict = await Assert.ThrowsAsync<ConcurrencyConflictException>(() => new UnitOfWork(handlerDb, _publisher).SaveChangesAsync(ct));

        Assert.Contains("Order", conflict.Message);
        await using var check = CreateContext();
        Assert.Equal(OrderStatus.Filled, (await new OrderRepository(check).GetAsync(order.Id, ct))!.Status);
        Assert.Single(_publisher.Published.OfType<OrderSubmitted>());
    }

    [Fact]
    public async Task Audit_entries_are_appended_in_their_own_scope_and_listed_newest_first()
    {
        var ct = TestContext.Current.CancellationToken;
        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection()
            .AddDbContext<TradingDbContext>(options => options.UseSqlite(_connection))
            .BuildServiceProvider();
        var log = new AuditLog(services.GetRequiredService<Microsoft.Extensions.DependencyInjection.IServiceScopeFactory>(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<AuditLog>.Instance);

        await log.RecordAsync(new AuditEntry(T0, TradingMode.Paper, AuditCategory.Order, "Created", "emt-1", "AAPL", "Buy 1 AAPL Market"), ct);
        await log.RecordAsync(new AuditEntry(T0.AddSeconds(1), TradingMode.Live, AuditCategory.Risk, "Halted", null, null, new string('x', 5000)), ct);

        var entries = await log.ListAsync(10, ct);

        Assert.Equal(["Halted", "Created"], entries.Select(e => e.Action));
        Assert.Equal((TradingMode.Live, AuditCategory.Risk, 4000), (entries[0].Mode, entries[0].Category, entries[0].Details.Length));
        Assert.Equal(("emt-1", "AAPL", T0), (entries[1].ClientOrderId, entries[1].Symbol, entries[1].Time));
    }

    [Fact]
    public async Task Instrument_list_is_replaced_as_a_whole_and_reports_its_age()
    {
        var ct = TestContext.Current.CancellationToken;
        await using (var db = CreateContext())
        {
            var store = new InstrumentStore(db);
            Assert.Equal(new InstrumentCatalogStatus(0, null), await store.GetStatusAsync(ct));

            await store.ReplaceAllAsync([new("MSFT", "Microsoft Corporation", "NASDAQ"), new("AAPL", "Apple Inc.", "NASDAQ")], T0, ct);
            await store.ReplaceAllAsync([new("SPY", "SPDR S&P 500 ETF", "ARCA"), new("AAPL", "Apple Inc.", "NASDAQ")], T0.AddDays(1), ct);
        }

        await using var check = CreateContext();
        var reader = new InstrumentStore(check);
        Assert.Equal(["AAPL", "SPY"], (await reader.ListAsync(ct)).Select(i => i.Symbol));
        Assert.Equal(new InstrumentCatalogStatus(2, T0.AddDays(1)), await reader.GetStatusAsync(ct));
    }

    [Fact]
    public async Task Screener_runs_round_trip_with_their_criteria_and_ranked_symbols()
    {
        var ct = TestContext.Current.CancellationToken;
        var criteria = ScreenerCriteria.Volatile with { MaxPrice = 250m, Exchanges = ["NASDAQ"] };
        var first = ScreenerRun(T0, criteria, ("MSFT", 300m, 1.5m), ("AAPL", 200m, null));
        var second = ScreenerRun(T0.AddDays(1), ScreenerCriteria.LiquidLargeCaps, ("SPY", 500m, 4m));

        await using (var db = CreateContext())
        {
            var store = new ScreenerResultStore(db);
            Assert.Null(await store.GetAsync(null, ct));
            await store.SaveAsync(first, ct);
            await store.SaveAsync(second, ct);
        }

        await using var check = CreateContext();
        var reader = new ScreenerResultStore(check);

        Assert.Equal([second.Summary.Id, first.Summary.Id], (await reader.ListRunsAsync(10, ct)).Select(r => r.Id));
        Assert.Equal(second.Summary.Id, (await reader.GetAsync(null, ct))!.Summary.Id);

        var loaded = (await reader.GetAsync(first.Summary.Id, ct))!;
        Assert.Equal(criteria.SortBy, loaded.Summary.Criteria.SortBy);
        Assert.Equal((criteria.MinAtrPercent, criteria.MaxPrice, criteria.MinTradingDays), (loaded.Summary.Criteria.MinAtrPercent, loaded.Summary.Criteria.MaxPrice, loaded.Summary.Criteria.MinTradingDays));
        Assert.Equal(["NASDAQ"], loaded.Summary.Criteria.Exchanges);
        Assert.Equal(first.Summary with { Criteria = loaded.Summary.Criteria }, loaded.Summary);
        Assert.Equal(first.Symbols, loaded.Symbols);
    }

    [Fact]
    public async Task Only_the_newest_screener_runs_are_kept()
    {
        var ct = TestContext.Current.CancellationToken;
        await using (var db = CreateContext())
        {
            var store = new ScreenerResultStore(db);
            for (var i = 0; i < ScreenerResultStore.KeepRuns + 2; i++)
                await store.SaveAsync(ScreenerRun(T0.AddMinutes(i), ScreenerCriteria.LiquidLargeCaps, ("AAPL", 200m, 1m)), ct);
        }

        await using var check = CreateContext();
        var runs = await new ScreenerResultStore(check).ListRunsAsync(100, ct);
        Assert.Equal(ScreenerResultStore.KeepRuns, runs.Count);
        Assert.Equal(T0.AddMinutes(2), runs[^1].CreatedAt);
        Assert.Equal(ScreenerResultStore.KeepRuns, await check.ScreenerRows.CountAsync(ct));
    }

    [Fact]
    public async Task Scan_with_candidates_and_evaluations_round_trips_and_lists_unfinished()
    {
        var ct = TestContext.Current.CancellationToken;
        var metrics = new PerformanceMetrics(20m, 8m, 1.2m, 12, 55m, 1.6m);
        var evaluation = new CandidateEvaluation(metrics, 8m, 11.1m, 10.5m, 25m, ScanRejection.LosingHalf | ScanRejection.DrawdownTooDeep);
        var screener = Guid.CreateVersion7(T0);
        var scan = ScanRun.Create(
            ScanRunId.New(T0),
            [Symbol.Create("AAPL"), Symbol.Create("MSFT")],
            [StrategyName.Create("SmaCrossStrategy")],
            DateRange.Create(new DateOnly(2024, 11, 1), new DateOnly(2026, 10, 30)),
            Money.Usd(50_000m),
            ScanThresholds.Create(4, 30m, false),
            maxParallel: 2,
            screener,
            T0);

        await using (var db = CreateContext())
        {
            new ScanRunRepository(db).Add(scan);
            await db.SaveChangesAsync(ct);
        }

        await using (var db = CreateContext())
        {
            var repository = new ScanRunRepository(db);
            Assert.Single(await repository.ListUnfinishedAsync(ct));
            var loaded = (await repository.GetAsync(scan.Id, ct))!;
            loaded.Start(T0);
            loaded.StartCandidate(0);
            loaded.CompleteCandidate(0, BacktestRunId.From("20261102-120000000_AAPL_SmaCrossStrategy"), evaluation, T0.AddMinutes(1));
            loaded.FailCandidate(1, "No market data", T0.AddMinutes(2));
            await db.SaveChangesAsync(ct);
        }

        var signal = StrategySignal.Classify(new DateOnly(2026, 10, 30), 200m, 40m, 195m, new DateTime(2026, 10, 30, 9, 31, 0), "Entry", 190m, null, null, "run-9");
        await using (var db = CreateContext())
        {
            var loaded = (await new ScanRunRepository(db).GetAsync(scan.Id, ct))!;
            loaded.RecordSignal(0, signal, T0.AddDays(1));
            await db.SaveChangesAsync(ct);
        }

        await using var check = CreateContext();
        var read = (await new ScanReader(check).FindAsync(scan.Id, ct))!;
        Assert.Equal(ScanStatus.Completed, read.Status);
        Assert.Equal(ScanThresholds.Create(4, 30m, false), read.Thresholds);
        Assert.Equal((50_000m, 2, screener), (read.InitialCapital.Amount, read.MaxParallel, read.ScreenerRunId));
        Assert.Equal(evaluation, read.Candidates[0].Evaluation);
        Assert.Equal("20261102-120000000_AAPL_SmaCrossStrategy", read.Candidates[0].RunId?.Value);
        Assert.Equal(signal, read.Candidates[0].Signal);
        Assert.Equal(T0.AddDays(1), read.Candidates[0].SignalCheckedAt);
        Assert.Equal((CandidateStatus.Failed, "No market data"), (read.Candidates[1].Status, read.Candidates[1].FailureReason));
        Assert.Empty(await new ScanRunRepository(check).ListUnfinishedAsync(ct));
        Assert.Equal([scan.Id], (await new ScanReader(check).ListAsync(5, ct)).Select(s => s.Id));
    }

    [Fact]
    public async Task Proposals_and_the_stop_loss_of_their_orders_round_trip()
    {
        var ct = TestContext.Current.CancellationToken;
        var signal = StrategySignal.Classify(new DateOnly(2026, 10, 30), 200m, 50m, 200m, new DateTime(2026, 10, 30, 16, 0, 0), "RSI dip", 190m, null, null, "run-7");
        var proposal = TradeProposal.Create(
            ProposalId.New(Symbol.Create("SPY"), T0), Symbol.Create("SPY"), StrategyName.Create("RsiPullbackStrategy"), signal,
            new PositionSize(100, 190m, 1_000m, SizeLimit.Risk, false), 100_000m, 3.2m, ScanRunId.New(T0), T0, T0.AddHours(8));
        var order = Order.Create(ClientOrderId.New(T0), Symbol.Create("SPY"), OrderSide.Buy, OrderType.Market, 100, null, null, TimeInForce.Day, T0, stopLossPrice: 190m);

        await using (var db = CreateContext())
        {
            new ProposalRepository(db).Add(proposal);
            db.Orders.Add(order);
            await db.SaveChangesAsync(ct);
        }

        await using (var db = CreateContext())
        {
            var repository = new ProposalRepository(db);
            var pending = Assert.Single(await repository.ListPendingAsync(ct));
            pending.Submit(order.Id, T0.AddMinutes(5));
            await db.SaveChangesAsync(ct);
        }

        await using var check = CreateContext();
        var read = Assert.Single(await new ProposalReader(check).ListAsync(10, ct));
        Assert.Equal((ProposalStatus.Submitted, order.Id, 100m, 190m, 3.2m), (read.Status, read.OrderId, read.Quantity, read.StopPrice, read.Score));
        Assert.Equal(signal, read.Signal);
        Assert.Equal(proposal.ScanId, read.ScanId);
        Assert.Equal(190m, (await check.Orders.SingleAsync(ct)).StopLossPrice);
    }

    [Fact]
    public async Task The_equity_peak_only_moves_up_and_is_kept_per_account()
    {
        var ct = TestContext.Current.CancellationToken;
        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection()
            .AddDbContext<TradingDbContext>(options => options.UseSqlite(_connection))
            .BuildServiceProvider();
        var store = new EquityPeakStore(services.GetRequiredService<Microsoft.Extensions.DependencyInjection.IServiceScopeFactory>());

        await store.SaveAsync(new Application.Trading.Portfolio.EquityPeak("PA1", 100_000m, T0), ct);
        await store.SaveAsync(new Application.Trading.Portfolio.EquityPeak("PA1", 90_000m, T0.AddDays(1)), ct);
        await store.SaveAsync(new Application.Trading.Portfolio.EquityPeak("PA1", 105_000m, T0.AddDays(2)), ct);
        await store.SaveAsync(new Application.Trading.Portfolio.EquityPeak("LIVE1", 5_000m, T0), ct);

        Assert.Equal((105_000m, T0.AddDays(2)), ((await store.GetAsync("PA1", ct))!.Equity, (await store.GetAsync("PA1", ct))!.At));
        Assert.Equal(5_000m, (await store.GetAsync("LIVE1", ct))!.Equity);
        Assert.Null(await store.GetAsync("OTHER", ct));
    }

    [Fact]
    public async Task The_autopilot_round_trips_with_its_pairs_and_their_simulated_positions()
    {
        var ct = TestContext.Current.CancellationToken;
        var rsi = StrategyName.Create("RsiPullbackStrategy");

        await using (var db = CreateContext())
        {
            var autopilot = await new AutopilotRepository(db).GetOrCreateAsync(ct);
            autopilot.SetMode(Domain.Autopilot.AutopilotMode.Shadow);
            autopilot.Configure(2, 40m);
            autopilot.Add(Symbol.Create("SPY"), rsi, 3.1m, T0);
            autopilot.Add(Symbol.Create("AAPL"), rsi, null, T0);
            await db.SaveChangesAsync(ct);
        }

        await using (var db = CreateContext())
        {
            var autopilot = await new AutopilotRepository(db).GetOrCreateAsync(ct);
            autopilot.Enter(1, 10, 200m, 190m, T0.AddDays(1));
            autopilot.Remove(2);
            autopilot.Add(Symbol.Create("MSFT"), rsi, 1.5m, T0);
            autopilot.CompleteCycle(new DateOnly(2026, 10, 16), T0.AddDays(1));
            await db.SaveChangesAsync(ct);
        }

        await using var check = CreateContext();
        var read = await new AutopilotRepository(check).GetOrCreateAsync(ct);
        Assert.Equal((Domain.Autopilot.AutopilotMode.Shadow, 2, 40m, new DateOnly(2026, 10, 16)), (read.Mode, read.MaxConsecutiveLosses, read.MaxCapitalPercent, read.LastCycleSession));
        Assert.Equal([(1, "SPY"), (3, "MSFT")], read.Assignments.OrderBy(a => a.Id).Select(a => (a.Id, a.Symbol.Value)));
        var spy = read.Assignments.Single(a => a.Id == 1);
        Assert.Equal((10m, 200m, 190m, T0.AddDays(1), 3.1m), (spy.Quantity, spy.EntryPrice, spy.StopPrice, spy.EntryAt, spy.Score));
        Assert.Equal(1, await check.Autopilots.CountAsync(ct));
    }

    [Fact]
    public async Task A_paper_position_round_trips_with_its_pending_entry_order()
    {
        var ct = TestContext.Current.CancellationToken;

        await using (var db = CreateContext())
        {
            var autopilot = await new AutopilotRepository(db).GetOrCreateAsync(ct);
            autopilot.SetMode(Domain.Autopilot.AutopilotMode.Paper);
            var id = autopilot.Add(Symbol.Create("SPY"), StrategyName.Create("RsiPullbackStrategy"), 3m, T0)!.Id;
            autopilot.Enter(id, 10, 200m, 190m, T0, "emt-20261015-0001");
            await db.SaveChangesAsync(ct);
        }

        await using var check = CreateContext();
        var a = (await new AutopilotRepository(check).GetOrCreateAsync(ct)).Assignments.Single();
        Assert.Equal((Domain.Autopilot.AutopilotMode.Paper, "emt-20261015-0001", true), (a.PositionMode, a.EntryOrderId, a.IsEntryPending));
    }

    [Fact]
    public async Task Autopilot_decisions_are_appended_and_listed_newest_first()
    {
        var ct = TestContext.Current.CancellationToken;
        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection()
            .AddDbContext<TradingDbContext>(options => options.UseSqlite(_connection))
            .BuildServiceProvider();
        var log = new AutopilotDecisionLog(services.GetRequiredService<Microsoft.Extensions.DependencyInjection.IServiceScopeFactory>());
        var buy = new Domain.Autopilot.AutopilotDecision(T0, Domain.Autopilot.AutopilotMode.Shadow, 1, "SPY", "RsiPullbackStrategy",
            Domain.Autopilot.AutopilotAction.Buy, 10, 200m, 190m, "Entry signal", false);
        var sell = buy with { Time = T0.AddDays(3), Action = Domain.Autopilot.AutopilotAction.Sell, Price = 210m, StopPrice = null, Profit = 100m };

        await log.AppendAsync([buy], ct);
        await log.AppendAsync([sell], ct);

        Assert.Equal([sell, buy], await log.ListAsync(10, ct));
        Assert.Equal([sell], await log.ListAsync(1, ct));
    }

    private static ScreenerResult ScreenerRun(DateTimeOffset createdAt, ScreenerCriteria criteria, params (string Symbol, decimal Close, decimal? Return6M)[] rows)
    {
        var symbols = rows.Select((r, i) => new ScreenedSymbol(i + 1, $"{r.Symbol} Inc.", "NASDAQ",
            new SymbolMetrics(r.Symbol, new DateOnly(2026, 10, 14), r.Close, 1_000_000m * r.Close, 2.5m, -1.25m, r.Return6M, -3.1m, 250))).ToList();
        var summary = new ScreenerRunSummary(Guid.CreateVersion7(createdAt), createdAt, new DateOnly(2026, 10, 14), criteria, 5000, 4800, symbols.Count);
        return new ScreenerResult(summary, symbols);
    }

    private static OptimizationRun CreateOptimization(DateTimeOffset createdAt)
    {
        var symbol = Symbol.Create("AAPL");
        var strategy = StrategyName.Create("SmaCrossStrategy");
        var grid = ParameterGrid.Create([ParameterRange.Create("fast-period", 10, 20, 10), ParameterRange.Create("risk-percent", 0.5m, 0.5m, 0.25m)]);
        return OptimizationRun.Create(
            OptimizationRunId.New(symbol, strategy, createdAt),
            symbol,
            strategy,
            DateRange.Create(new DateOnly(2026, 1, 2), new DateOnly(2026, 9, 30)),
            Money.Usd(50_000m),
            grid,
            OptimizationObjective.ReturnOverDrawdown,
            maxParallelTrials: 3,
            [
                StrategyParameters.Create([new("fast-period", "10"), new("risk-percent", "0.5"), new("slow-period", "130")]),
                StrategyParameters.Create([new("fast-period", "20"), new("risk-percent", "0.5"), new("slow-period", "130")])
            ],
            createdAt);
    }

    private TradingDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<TradingDbContext>().UseSqlite(_connection).Options);

    private async Task SaveAsync(BacktestRun run)
    {
        await using var db = CreateContext();
        new BacktestRunRepository(db).Add(run);
        await new UnitOfWork(db, _publisher).SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    private static BacktestRun CreateRun(string ticker, DateTimeOffset createdAt, OptimizationRunId? optimizationId = null)
    {
        var symbol = Symbol.Create(ticker);
        var strategy = StrategyName.Create("SmaCrossStrategy");
        return BacktestRun.Create(
            BacktestRunId.New(symbol, strategy, createdAt),
            symbol,
            strategy,
            DateRange.Create(new DateOnly(2026, 1, 2), new DateOnly(2026, 9, 30)),
            Money.Usd(100_000m),
            StrategyParameters.Create([new("fast-period", "26"), new("slow-period", "130")]),
            createdAt,
            optimizationId);
    }

    private sealed class RecordingPublisher : IDomainEventPublisher
    {
        public List<IDomainEvent> Published { get; } = [];

        public Action? OnPublish { get; set; }

        public Task PublishAsync(IEnumerable<IDomainEvent> domainEvents, CancellationToken cancellationToken = default)
        {
            var events = domainEvents.ToList();
            if (events.Count > 0)
                OnPublish?.Invoke();

            Published.AddRange(events);
            return Task.CompletedTask;
        }
    }
}
