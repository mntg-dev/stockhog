using Stockhog.Application.Abstractions.Jobs;
using Stockhog.Application.Abstractions.Messaging;
using Stockhog.Application.Abstractions.Persistence;
using Stockhog.Application.Abstractions.Results;
using Stockhog.Application.Analytics;
using Microsoft.Extensions.Logging;

namespace Stockhog.Application.Backtesting.BackfillMetrics;

/// <summary>
/// Adds metrics to completed runs stored before runs kept them, by reading each run's result once. Works in batches
/// and saves after each, so a large history is processed with bounded memory and progress survives a restart.
/// </summary>
/// <returns>How many runs got metrics.</returns>
public sealed record BackfillBacktestMetricsCommand : ICommand<int>
{
    public const int BatchSize = 100;

    public IProgress<JobProgress>? Progress { get; init; }
}

/// <summary>Queues <see cref="BackfillBacktestMetricsCommand"/> as a background job, only when some run needs it.</summary>
public sealed record QueueMetricsBackfillCommand : ICommand<bool>
{
    /// <summary>Starts with "backtest:", so pages showing runs refresh when it finishes.</summary>
    public const string JobKey = "backtest:metrics-backfill";
}

internal sealed class BackfillBacktestMetricsCommandHandler(
    IBacktestRunRepository repository,
    IBacktestResultReader results,
    IUnitOfWork unitOfWork,
    ILogger<BackfillBacktestMetricsCommandHandler> logger) : ICommandHandler<BackfillBacktestMetricsCommand, int>
{
    public async Task<Result<int>> HandleAsync(BackfillBacktestMetricsCommand command, CancellationToken cancellationToken)
    {
        var total = await repository.CountCompletedWithoutMetricsAsync(cancellationToken);
        var updated = 0;
        var attempted = 0;

        // Runs whose result is missing keep null metrics; skipping past them keeps the loop finite.
        while (attempted < total)
        {
            var batch = await repository.ListCompletedWithoutMetricsAsync(attempted - updated + BackfillBacktestMetricsCommand.BatchSize, cancellationToken);
            var pending = batch.Skip(attempted - updated).ToList();
            if (pending.Count == 0)
                break;

            foreach (var run in pending)
            {
                attempted++;
                try
                {
                    if (await results.ReadAsync(run.Symbol, run.Id, cancellationToken) is { } report)
                    {
                        run.RecordMetrics(PerformanceCalculator.Calculate(report));
                        updated++;
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning(ex, "Could not read the result of run {RunId}; its metrics stay empty", run.Id);
                }
            }

            await unitOfWork.SaveChangesAsync(cancellationToken);
            command.Progress?.Report(new JobProgress(total == 0 ? 100 : attempted * 100 / total, $"{attempted} of {total} runs"));
        }

        return updated;
    }
}

internal sealed class QueueMetricsBackfillCommandHandler(IBacktestRunRepository repository, IJobQueue jobs)
    : ICommandHandler<QueueMetricsBackfillCommand, bool>
{
    public async Task<Result<bool>> HandleAsync(QueueMetricsBackfillCommand command, CancellationToken cancellationToken)
    {
        if (await repository.CountCompletedWithoutMetricsAsync(cancellationToken) == 0)
            return false;

        return jobs.Enqueue("Calculate metrics of earlier runs", RunAsync, QueueMetricsBackfillCommand.JobKey).WasQueued;
    }

    private static async Task<JobOutcome> RunAsync(JobExecutionContext context)
    {
        var result = await context.Dispatcher.SendAsync(new BackfillBacktestMetricsCommand { Progress = context.Progress }, context.CancellationToken);
        return result.Match(count => JobOutcome.Succeeded($"Metrics added to {count} run(s)."), error => JobOutcome.Failed(error.Message));
    }
}
