using Stockhog.Application.Abstractions.Jobs;
using Stockhog.Application.Abstractions.Messaging;
using Stockhog.Application.Abstractions.Persistence;
using Stockhog.Application.Abstractions.Results;
using Stockhog.Application.Backtesting.RunBacktest;
using Stockhog.Application.Common;
using Stockhog.Application.Optimization.ExecuteOptimization;
using Stockhog.Domain.Backtesting;
using Stockhog.Domain.Common;
using Stockhog.Domain.Optimization;
using FluentValidation;
using Microsoft.Extensions.Logging;

namespace Stockhog.Application.Optimization.CheckOptimization;

/// <summary>
/// Backtests the <paramref name="Top"/> best trials of a finished optimization again on a period it did not see, as
/// one background job. Parameters that hold up there are more trustworthy than the best score of the optimization,
/// which partly fits the noise of its own period. The runs are normal backtests and show up in the history.
/// </summary>
public sealed record QueueOptimizationCheckCommand(string OptimizationId, DateOnly Start, DateOnly End, int Top = QueueOptimizationCheckCommand.DefaultTop)
    : ICommand<QueuedOptimizationCheck>
{
    public const int DefaultTop = 5;
}

public sealed record QueuedOptimizationCheck(Guid JobId, int Trials);

internal sealed class QueueOptimizationCheckCommandValidator : AbstractValidator<QueueOptimizationCheckCommand>
{
    public QueueOptimizationCheckCommandValidator(TimeProvider clock)
    {
        RuleFor(c => c.OptimizationId).Must(OptimizationRunId.IsValid).WithMessage("'{PropertyValue}' is not a valid optimization id.");
        RuleFor(c => c.End)
            .GreaterThanOrEqualTo(c => c.Start).WithMessage("End date must not be before start date.")
            .MustNotBeInTheFuture(clock);
        RuleFor(c => c.Top).InclusiveBetween(1, OptimizationRun.MaxCheckedTrials)
            .WithMessage($"Between 1 and {OptimizationRun.MaxCheckedTrials} trials can be checked.");
    }
}

internal sealed class QueueOptimizationCheckCommandHandler(IOptimizationReader reader, IJobQueue jobs)
    : ICommandHandler<QueueOptimizationCheckCommand, QueuedOptimizationCheck>
{
    public async Task<Result<QueuedOptimizationCheck>> HandleAsync(QueueOptimizationCheckCommand command, CancellationToken cancellationToken)
    {
        // An untracked copy: starting the check on it applies the domain rules without saving anything. The job
        // starts the check for real, so a check that cannot be queued never clears the previous one.
        var run = await reader.FindAsync(OptimizationRunId.From(command.OptimizationId), cancellationToken);
        if (run is null)
            return OptimizationErrors.NotFound(command.OptimizationId);

        IReadOnlyList<int> trials;
        try
        {
            trials = run.StartCheck(DateRange.Create(command.Start, command.End), command.Top);
        }
        catch (DomainException ex)
        {
            return Error.Validation([ex.Message]);
        }

        var execute = new RunOptimizationCheckCommand(command.OptimizationId, command.Start, command.End, command.Top);

        // Same key as a backtest of this symbol and strategy: run ids are timestamp-based and must stay unique, and
        // it also keeps two checks of the same optimization from running at once.
        var job = jobs.Enqueue(
            $"Check {run.Strategy.Value} {run.Symbol.Value} on {command.Start:yyyy-MM-dd}..{command.End:yyyy-MM-dd} ({trials.Count} trials)",
            context => ExecuteAsync(execute, context),
            $"backtest:{run.Symbol.Value}:{run.Strategy.Value}");

        if (!job.WasQueued)
        {
            return new Error("Optimization.CheckBusy",
                $"A backtest or check of {run.Strategy.Value} on {run.Symbol.Value} is already running; try again when it finishes.",
                ErrorType.Conflict);
        }

        return new QueuedOptimizationCheck(job.Job.Id, trials.Count);
    }

    private static async Task<JobOutcome> ExecuteAsync(RunOptimizationCheckCommand command, JobExecutionContext context)
    {
        var result = await context.Dispatcher.SendAsync(
            command with
            {
                Progress = new DelegateProgress<OptimizationProgress>(p =>
                    context.Progress.Report(new JobProgress(p.Percent, $"{p.FinishedTrials}/{p.TotalTrials} trials")))
            },
            context.CancellationToken);

        return result.Match(
            outcome => outcome.Completed > 0
                ? JobOutcome.Succeeded($"{outcome.Completed}/{outcome.Total} trials checked.")
                : JobOutcome.Failed("No trial could be checked."),
            error => JobOutcome.Failed(error.Message));
    }
}

/// <summary>Sent by the check's background job: starts the check and backtests its trials one after another.</summary>
public sealed record RunOptimizationCheckCommand(string OptimizationId, DateOnly Start, DateOnly End, int Top) : ICommand<OptimizationCheckOutcome>
{
    /// <summary>Optional live progress for the caller; not part of the command's data.</summary>
    public IProgress<OptimizationProgress>? Progress { get; init; }
}

public sealed record OptimizationCheckOutcome(int Total, int Completed);

internal sealed class RunOptimizationCheckCommandValidator : AbstractValidator<RunOptimizationCheckCommand>
{
    public RunOptimizationCheckCommandValidator() =>
        RuleFor(c => c.OptimizationId).Must(OptimizationRunId.IsValid).WithMessage("'{PropertyValue}' is not a valid optimization id.");
}

/// <summary>
/// One trial at a time: the backtests share a symbol and strategy, and their timestamp-based run ids must differ.
/// The optimization is saved after every trial, so finished checks survive a crash.
/// </summary>
internal sealed class RunOptimizationCheckCommandHandler(
    IOptimizationRunRepository repository,
    IUnitOfWork unitOfWork,
    IDispatcher dispatcher,
    ILogger<RunOptimizationCheckCommandHandler> logger) : ICommandHandler<RunOptimizationCheckCommand, OptimizationCheckOutcome>
{
    public async Task<Result<OptimizationCheckOutcome>> HandleAsync(RunOptimizationCheckCommand command, CancellationToken cancellationToken)
    {
        var run = await repository.GetAsync(OptimizationRunId.From(command.OptimizationId), cancellationToken);
        if (run is null)
            return OptimizationErrors.NotFound(command.OptimizationId);

        var period = DateRange.Create(command.Start, command.End);
        var trials = run.StartCheck(period, command.Top);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        var completed = 0;
        command.Progress?.Report(new OptimizationProgress(0, trials.Count));
        try
        {
            for (var i = 0; i < trials.Count; i++)
            {
                if (await CheckTrialAsync(run, trials[i], period, cancellationToken))
                    completed++;
                command.Progress?.Report(new OptimizationProgress(i + 1, trials.Count));
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            run.CancelCheck();
            await unitOfWork.SaveChangesAsync(CancellationToken.None);
            throw;
        }

        return new OptimizationCheckOutcome(trials.Count, completed);
    }

    /// <summary>A failing backtest is recorded and the check goes on with the next trial.</summary>
    private async Task<bool> CheckTrialAsync(OptimizationRun run, int index, DateRange period, CancellationToken cancellationToken)
    {
        run.StartTrialCheck(index);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        try
        {
            var backtest = await dispatcher.SendAsync(
                new RunBacktestCommand(run.Strategy.Value, run.Symbol.Value, period.Start, period.End, run.InitialCapital.Amount,
                    run.Trials[index].Parameters.Values),
                cancellationToken);

            if (backtest.IsFailure)
                run.FailTrialCheck(index, backtest.Error.Message);
            else if (backtest.Value.Metrics is not { } metrics)
                run.FailTrialCheck(index, $"No result was found for run {backtest.Value.RunId}.");
            else
                run.CompleteTrialCheck(index, BacktestRunId.From(backtest.Value.RunId), metrics);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Check of trial {Index} of optimization {OptimizationId} failed", index, run.Id);
            run.FailTrialCheck(index, ex.Message);
        }

        await unitOfWork.SaveChangesAsync(CancellationToken.None);
        return run.Trials[index].CheckStatus == TrialStatus.Completed;
    }
}
