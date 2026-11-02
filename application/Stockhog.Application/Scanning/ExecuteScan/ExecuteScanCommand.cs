using Stockhog.Application.Abstractions.Messaging;
using Stockhog.Application.Abstractions.Persistence;
using Stockhog.Application.Abstractions.Results;
using Stockhog.Application.Backtesting;
using Stockhog.Application.Backtesting.RunBacktest;
using Stockhog.Application.MarketData.DownloadMarketData;
using Stockhog.Application.Screening;
using Stockhog.Domain.Backtesting;
using Stockhog.Domain.MarketData;
using Stockhog.Domain.Scanning;
using FluentValidation;
using Microsoft.Extensions.Logging;

namespace Stockhog.Application.Scanning.ExecuteScan;

/// <summary>Runs a queued scan; sent by its background job.</summary>
public sealed record ExecuteScanCommand(string ScanId) : ICommand<ScanOutcome>
{
    /// <summary>Optional live progress for the caller; not part of the command's data.</summary>
    public IProgress<ScanProgress>? Progress { get; init; }
}

/// <param name="Stage">What the scan is doing: preparing data, or evaluating candidates.</param>
public readonly record struct ScanProgress(ScanStage Stage, int Done, int Total)
{
    /// <summary>Data takes the first fifth of the bar, backtests the rest.</summary>
    public int Percent => Total == 0
        ? 100
        : Stage == ScanStage.Data ? Done * 20 / Total : 20 + Done * 80 / Total;

    public string Describe() => Stage == ScanStage.Data
        ? $"Data {Done}/{Total} symbols"
        : $"{Done}/{Total} candidates";
}

public enum ScanStage
{
    Data,
    Backtests
}

public sealed record ScanOutcome(string ScanId, ScanStatus Status, int Total, int Evaluated, int Passed, string? FailureReason);

internal sealed class ExecuteScanCommandValidator : AbstractValidator<ExecuteScanCommand>
{
    public ExecuteScanCommandValidator() =>
        RuleFor(c => c.ScanId).Must(ScanRunId.IsValid).WithMessage("'{PropertyValue}' is not a valid scan id.");
}

/// <summary>
/// Three steps: minute data for every symbol (only missing days are downloaded), the stocks' own price change over the
/// period for comparison, then one backtest per candidate with the strategy's defaults, evaluated as it finishes.
/// Candidates run with bounded parallelism, each <see cref="RunBacktestCommand"/> in its own scope; the aggregate and
/// this handler's unit of work are shared, so every change goes through one async lock and is saved straight away.
/// </summary>
internal sealed class ExecuteScanCommandHandler(
    IScanRunRepository repository,
    IUnitOfWork unitOfWork,
    IDispatcher dispatcher,
    IBacktestResultReader results,
    IDailyBarSource dailyBars,
    TimeProvider clock,
    ILogger<ExecuteScanCommandHandler> logger) : ICommandHandler<ExecuteScanCommand, ScanOutcome>
{
    /// <summary>Symbols downloaded at the same time; the shared rate limiter keeps them within the plan's limit.</summary>
    private const int ParallelDownloads = 4;

    public async Task<Result<ScanOutcome>> HandleAsync(ExecuteScanCommand command, CancellationToken cancellationToken)
    {
        var run = await repository.GetAsync(ScanRunId.From(command.ScanId), cancellationToken);
        if (run is null)
            return ScanErrors.NotFound(command.ScanId);
        if (run.Status != ScanStatus.Queued)
            return ScanErrors.AlreadyFinished(command.ScanId, run.Status);

        run.Start(clock.GetUtcNow());
        await unitOfWork.SaveChangesAsync(cancellationToken);

        using var gate = new SemaphoreSlim(1, 1);
        try
        {
            var withData = await PrepareDataAsync(run, gate, command.Progress, cancellationToken);
            var buyAndHold = await BuyAndHoldAsync(run, withData, cancellationToken);
            await EvaluateCandidatesAsync(run, withData, buyAndHold, gate, command.Progress, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await ChangeAsync(gate, () =>
            {
                if (!run.IsFinished)
                    run.Cancel(clock.GetUtcNow());
            });
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await ChangeAsync(gate, () =>
            {
                if (!run.IsFinished)
                    run.Fail(ex.Message, clock.GetUtcNow());
            });
            throw;
        }

        return new ScanOutcome(
            run.Id.Value,
            run.Status,
            run.Candidates.Count,
            run.Candidates.Count(c => c.Status == CandidateStatus.Evaluated),
            run.PassedCandidates,
            run.FailureReason);
    }

    /// <returns>Symbols whose data is ready. Candidates of the others are failed with the download's error.</returns>
    private async Task<HashSet<Symbol>> PrepareDataAsync(ScanRun run, SemaphoreSlim gate, IProgress<ScanProgress>? progress, CancellationToken cancellationToken)
    {
        var symbols = run.Symbols;
        var ready = new HashSet<Symbol>();
        var done = 0;
        progress?.Report(new ScanProgress(ScanStage.Data, 0, symbols.Count));

        var parallel = new ParallelOptions { MaxDegreeOfParallelism = ParallelDownloads, CancellationToken = cancellationToken };
        await Parallel.ForEachAsync(symbols, parallel, async (symbol, token) =>
        {
            string? error;
            try
            {
                var download = await dispatcher.SendAsync(
                    new DownloadMarketDataCommand(symbol.Value, Timeframe.Minute, run.Period.Start, run.Period.End), token);
                error = download.IsFailure ? download.Error.Message : null;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Scan {ScanId}: could not download {Symbol}", run.Id, symbol);
                error = ex.Message;
            }

            await ChangeAsync(gate, () =>
            {
                if (error is null)
                {
                    ready.Add(symbol);
                    return;
                }

                foreach (var candidate in run.Candidates.Where(c => c.Symbol == symbol && !c.IsFinished))
                    run.FailCandidate(candidate.Index, $"No market data: {error}", clock.GetUtcNow());
            });
            progress?.Report(new ScanProgress(ScanStage.Data, Interlocked.Increment(ref done), symbols.Count));
        });

        return ready;
    }

    /// <summary>Close-to-close price change of each symbol over the period, in percent. Missing when it cannot be known.</summary>
    private async Task<IReadOnlyDictionary<string, decimal>> BuyAndHoldAsync(ScanRun run, HashSet<Symbol> symbols, CancellationToken cancellationToken)
    {
        var changes = new Dictionary<string, decimal>(StringComparer.Ordinal);
        if (!dailyBars.IsConfigured || symbols.Count == 0)
            return changes;

        try
        {
            foreach (var batch in symbols.Select(s => s.Value).Chunk(Math.Max(1, dailyBars.MaxSymbolsPerRequest)))
            {
                var bars = await dailyBars.GetDailyBarsAsync(batch, run.Period.Start, run.Period.End, cancellationToken);
                foreach (var (symbol, symbolBars) in bars)
                {
                    if (symbolBars.Count >= 2 && symbolBars[0].Close > 0)
                        changes[symbol] = Math.Round((symbolBars[^1].Close - symbolBars[0].Close) / symbolBars[0].Close * 100m, 2);
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Only a comparison: the scan goes on without it.
            logger.LogWarning(ex, "Scan {ScanId}: could not get daily bars for buy and hold", run.Id);
        }

        return changes;
    }

    private async Task EvaluateCandidatesAsync(
        ScanRun run,
        HashSet<Symbol> withData,
        IReadOnlyDictionary<string, decimal> buyAndHold,
        SemaphoreSlim gate,
        IProgress<ScanProgress>? progress,
        CancellationToken cancellationToken)
    {
        var pending = run.Candidates
            .Where(c => c.Status == CandidateStatus.Pending && withData.Contains(c.Symbol))
            .Select(c => (c.Index, c.Symbol, c.Strategy))
            .ToList();
        var done = 0;
        progress?.Report(new ScanProgress(ScanStage.Backtests, 0, pending.Count));

        var parallel = new ParallelOptions { MaxDegreeOfParallelism = run.MaxParallel, CancellationToken = cancellationToken };
        await Parallel.ForEachAsync(pending, parallel, async (candidate, token) =>
        {
            decimal? hold = buyAndHold.TryGetValue(candidate.Symbol.Value, out var change) ? change : null;
            await EvaluateAsync(run, candidate.Index, candidate.Symbol, candidate.Strategy, hold, gate, token);
            progress?.Report(new ScanProgress(ScanStage.Backtests, Interlocked.Increment(ref done), pending.Count));
        });
    }

    /// <summary>A failing candidate is recorded and the others continue; only cancellation stops the scan.</summary>
    private async Task EvaluateAsync(ScanRun run, int index, Symbol symbol, StrategyName strategy, decimal? buyAndHold, SemaphoreSlim gate, CancellationToken cancellationToken)
    {
        await ChangeAsync(gate, () => run.StartCandidate(index));

        try
        {
            var backtest = await dispatcher.SendAsync(
                new RunBacktestCommand(strategy.Value, symbol.Value, run.Period.Start, run.Period.End, run.InitialCapital.Amount, new Dictionary<string, string>()),
                cancellationToken);
            if (backtest.IsFailure)
            {
                await ChangeAsync(gate, () => run.FailCandidate(index, backtest.Error.Message, clock.GetUtcNow()));
                return;
            }

            var runId = BacktestRunId.From(backtest.Value.RunId);
            var report = await results.ReadAsync(symbol, runId, cancellationToken);
            if (report is null || backtest.Value.Metrics is not { } metrics)
            {
                await ChangeAsync(gate, () => run.FailCandidate(index, $"No result was found for run {runId}.", clock.GetUtcNow()));
                return;
            }

            var evaluation = ScanEvaluator.Evaluate(report, metrics, run.Period, run.InitialCapital.Amount, buyAndHold, run.Thresholds, cancellationToken);
            await ChangeAsync(gate, () => run.CompleteCandidate(index, runId, evaluation, clock.GetUtcNow()));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Candidate {Index} ({Symbol}, {Strategy}) of scan {ScanId} failed", index, symbol, strategy, run.Id);
            await ChangeAsync(gate, () => run.FailCandidate(index, ex.Message, clock.GetUtcNow()));
        }
    }

    /// <summary>Applies a change and saves it while holding the lock; runs to completion even when cancelled.</summary>
    private async Task ChangeAsync(SemaphoreSlim gate, Action change)
    {
        await gate.WaitAsync(CancellationToken.None);
        try
        {
            change();
            await unitOfWork.SaveChangesAsync(CancellationToken.None);
        }
        finally
        {
            gate.Release();
        }
    }
}
