using Stockhog.Application.Abstractions.Messaging;
using Stockhog.Application.Abstractions.Persistence;
using Stockhog.Application.Abstractions.Results;
using Stockhog.Application.Analytics;
using Stockhog.Application.Backtesting.Strategies;
using Stockhog.Domain.Backtesting;
using Stockhog.Domain.Common;
using Stockhog.Domain.MarketData;
using Stockhog.Domain.Optimization;
using Microsoft.Extensions.Logging;

namespace Stockhog.Application.Backtesting.RunBacktest;

/// <summary>
/// Saves the run as Running before the engine starts, so it shows up in the history straight away,
/// and saves its final state on every outcome: completed, failed, cancelled or crashed.
/// </summary>
internal sealed class RunBacktestCommandHandler(
    IBacktestEngine engine,
    IBacktestResultReader results,
    IBacktestRunRepository repository,
    IUnitOfWork unitOfWork,
    IStrategyCatalog strategies,
    TimeProvider clock,
    ILogger<RunBacktestCommandHandler> logger) : ICommandHandler<RunBacktestCommand, BacktestRunResult>
{
    public async Task<Result<BacktestRunResult>> HandleAsync(RunBacktestCommand command, CancellationToken cancellationToken)
    {
        var symbol = Symbol.Create(command.Symbol);
        var strategy = StrategyName.Create(command.StrategyName);

        // The validator has checked the strategy exists; storing every value (defaults included)
        // keeps the run reproducible even if a default changes later.
        var parameters = strategies.Find(strategy.Value)!.WithDefaults(command.Parameters);

        var optimization = command.Trial is { } trial ? OptimizationRunId.From(trial.OptimizationId) : null;
        var runId = optimization is null
            ? BacktestRunId.New(symbol, strategy, clock.GetLocalNow())
            : BacktestRunId.ForTrial(optimization, command.Trial!.Index);

        var run = BacktestRun.Create(
            runId,
            symbol,
            strategy,
            DateRange.Create(command.Start, command.End),
            Money.Usd(command.InitialCapital),
            StrategyParameters.Create(parameters),
            clock.GetUtcNow(),
            optimization);

        run.Start(clock.GetUtcNow());
        repository.Add(run);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        // From here on the run exists in storage, so every outcome is saved, even if the caller has cancelled.
        try
        {
            await engine.RunAsync(BacktestRequest.For(run), new RunProgress(run, command.Progress), cancellationToken);
            run.Complete(clock.GetUtcNow(), await ReadMetricsAsync(run, cancellationToken));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            run.Cancel(clock.GetUtcNow());
            await unitOfWork.SaveChangesAsync(CancellationToken.None);
            throw;
        }
        catch (BacktestEngineException ex)
        {
            run.Fail(ex.Message, clock.GetUtcNow());
            await unitOfWork.SaveChangesAsync(CancellationToken.None);
            return BacktestErrors.EngineFailed(ex.Message);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Unexpected: record the failure so the run never stays "Running", then let the exception surface.
            run.Fail(ex.Message, clock.GetUtcNow());
            await unitOfWork.SaveChangesAsync(CancellationToken.None);
            throw;
        }

        await unitOfWork.SaveChangesAsync(CancellationToken.None);
        return new BacktestRunResult(run.Id.Value, symbol.Value, strategy.Value, run.Metrics);
    }

    /// <summary>
    /// The run's key figures, stored with it so runs can be ranked without reading every result file. A result that
    /// cannot be read does not fail the run: the report itself is still there to look at.
    /// </summary>
    private async Task<PerformanceMetrics?> ReadMetricsAsync(BacktestRun run, CancellationToken cancellationToken)
    {
        try
        {
            var report = await results.ReadAsync(run.Symbol, run.Id, cancellationToken);
            return report is null ? null : PerformanceCalculator.Calculate(report);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Could not read the result of run {RunId}; it is stored without metrics", run.Id);
            return null;
        }
    }

    /// <summary>
    /// Records progress on the aggregate and forwards it to the caller.
    /// The engine reports from one output thread and finishes reporting before RunAsync returns,
    /// and nothing is saved while it runs, so the aggregate is never touched by two threads at once.
    /// </summary>
    private sealed class RunProgress(BacktestRun run, IProgress<BacktestProgress>? caller) : IProgress<BacktestProgress>
    {
        public void Report(BacktestProgress value)
        {
            run.ReportProgress(value.Percent);
            caller?.Report(value);
        }
    }
}
