using Stockhog.Application.Abstractions.Messaging;
using Stockhog.Application.Abstractions.Persistence;
using Stockhog.Application.Abstractions.Results;
using Stockhog.Application.Backtesting;
using Stockhog.Application.Backtesting.RunBacktest;
using Stockhog.Domain.Backtesting;
using Stockhog.Domain.Optimization;
using FluentValidation;
using Microsoft.Extensions.Logging;

namespace Stockhog.Application.Optimization.ExecuteOptimization;

/// <summary>
/// Runs the pending trials of a queued optimization; sent by its background job.
/// </summary>
public sealed record ExecuteOptimizationCommand(string OptimizationId) : ICommand<OptimizationOutcome>
{
    /// <summary>Optional live progress for the caller; not part of the command's data.</summary>
    public IProgress<OptimizationProgress>? Progress { get; init; }
}

public readonly record struct OptimizationProgress(int FinishedTrials, int TotalTrials)
{
    public int Percent => TotalTrials == 0 ? 100 : FinishedTrials * 100 / TotalTrials;
}

public sealed record OptimizationOutcome(
    string OptimizationId,
    OptimizationStatus Status,
    int TotalTrials,
    int CompletedTrials,
    int? BestTrialIndex,
    decimal? BestScore,
    string? FailureReason);

internal sealed class ExecuteOptimizationCommandValidator : AbstractValidator<ExecuteOptimizationCommand>
{
    public ExecuteOptimizationCommandValidator() =>
        RuleFor(c => c.OptimizationId).Must(OptimizationRunId.IsValid).WithMessage("'{PropertyValue}' is not a valid optimization id.");
}

/// <summary>
/// Trials run with bounded parallelism (<see cref="OptimizationRun.MaxParallelTrials"/> engine processes at once),
/// each as a normal <see cref="RunBacktestCommand"/> in its own scope. The aggregate and this handler's unit of work
/// are shared by the trial workers, so every change to them goes through one async lock; the optimization is saved
/// after every trial, so finished trials survive a crash.
/// </summary>
internal sealed class ExecuteOptimizationCommandHandler(
    IOptimizationRunRepository repository,
    IUnitOfWork unitOfWork,
    IDispatcher dispatcher,
    TimeProvider clock,
    ILogger<ExecuteOptimizationCommandHandler> logger) : ICommandHandler<ExecuteOptimizationCommand, OptimizationOutcome>
{
    public async Task<Result<OptimizationOutcome>> HandleAsync(ExecuteOptimizationCommand command, CancellationToken cancellationToken)
    {
        var run = await repository.GetAsync(OptimizationRunId.From(command.OptimizationId), cancellationToken);
        if (run is null)
            return OptimizationErrors.NotFound(command.OptimizationId);
        if (run.Status != OptimizationStatus.Queued)
            return OptimizationErrors.AlreadyFinished(command.OptimizationId, run.Status);

        run.Start(clock.GetUtcNow());
        await unitOfWork.SaveChangesAsync(cancellationToken);

        using var gate = new SemaphoreSlim(1, 1);
        var finished = 0;
        var pending = run.Trials.Select(t => (t.Index, t.Parameters)).ToList();
        command.Progress?.Report(new OptimizationProgress(0, pending.Count));

        var parallel = new ParallelOptions { MaxDegreeOfParallelism = run.MaxParallelTrials, CancellationToken = cancellationToken };

        try
        {
            await Parallel.ForEachAsync(pending, parallel, async (trial, token) =>
            {
                await RunTrialAsync(run, trial.Index, trial.Parameters, gate, token);
                command.Progress?.Report(new OptimizationProgress(Interlocked.Increment(ref finished), pending.Count));
            });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The last trial may have finished the optimization just before the cancellation was observed.
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

        return new OptimizationOutcome(
            run.Id.Value,
            run.Status,
            run.Trials.Count,
            run.Trials.Count(t => t.Status == TrialStatus.Completed),
            run.BestTrialIndex,
            run.BestTrial?.Metrics?.Score(run.Objective),
            run.FailureReason);
    }

    /// <summary>A failing trial is recorded and the others continue; only cancellation stops the optimization.</summary>
    private async Task RunTrialAsync(OptimizationRun run, int index, StrategyParameters parameters, SemaphoreSlim gate, CancellationToken cancellationToken)
    {
        var runId = BacktestRunId.ForTrial(run.Id, index);
        await ChangeAsync(gate, () => run.StartTrial(index, runId));

        try
        {
            var backtest = await dispatcher.SendAsync(
                new RunBacktestCommand(run.Strategy.Value, run.Symbol.Value, run.Period.Start, run.Period.End, run.InitialCapital.Amount, parameters.Values)
                {
                    Trial = new OptimizationTrialReference(run.Id.Value, index)
                },
                cancellationToken);

            if (backtest.IsFailure)
            {
                await ChangeAsync(gate, () => run.FailTrial(index, backtest.Error.Message, clock.GetUtcNow()));
                return;
            }

            // The run computed its metrics from its result when it completed.
            var metrics = backtest.Value.Metrics;
            await ChangeAsync(gate, () =>
            {
                if (metrics is null)
                    run.FailTrial(index, $"No result was found for run {runId}.", clock.GetUtcNow());
                else
                    run.CompleteTrial(index, metrics, clock.GetUtcNow());
            });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Trial {Index} of optimization {OptimizationId} failed", index, run.Id);
            await ChangeAsync(gate, () => run.FailTrial(index, ex.Message, clock.GetUtcNow()));
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
