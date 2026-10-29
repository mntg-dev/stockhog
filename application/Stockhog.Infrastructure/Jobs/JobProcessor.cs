using Stockhog.Application.Abstractions.Jobs;
using Stockhog.Application.Abstractions.Messaging;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Stockhog.Infrastructure.Jobs;

/// <summary>
/// Consumer side of the job pipeline: reads the queue and runs up to <see cref="JobOptions.MaxParallelJobs"/> jobs at once.
/// When the application stops, running jobs are cancelled and marked as such.
/// </summary>
internal sealed class JobProcessor(
    JobQueue queue,
    IDispatcher dispatcher,
    IOptions<JobOptions> options,
    TimeProvider clock,
    ILogger<JobProcessor> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var parallel = new ParallelOptions
        {
            MaxDegreeOfParallelism = options.Value.MaxParallelJobs,
            CancellationToken = stoppingToken
        };

        logger.LogInformation("Job processor started with {MaxParallelJobs} parallel job(s)", parallel.MaxDegreeOfParallelism);

        try
        {
            await Parallel.ForEachAsync(queue.Reader.ReadAllAsync(stoppingToken), parallel, RunAsync);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal shutdown.
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        queue.Complete();
        await base.StopAsync(cancellationToken);
    }

    /// <summary>Never throws: one failing job must not stop the processing loop.</summary>
    private async ValueTask RunAsync(JobEntry job, CancellationToken stoppingToken)
    {
        if (!job.TryStart(clock.GetUtcNow()))
            return;

        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(job.CancellationToken, stoppingToken);
        var context = new JobExecutionContext(dispatcher, new JobProgressReporter(job), cancellation.Token);

        try
        {
            logger.LogInformation("Job started: {Title}", job.Title);
            var outcome = await job.Work(context);
            job.Finish(outcome.IsSuccess ? JobStatus.Succeeded : JobStatus.Failed, outcome.Message, clock.GetUtcNow());
            logger.LogInformation("Job {Outcome}: {Title}: {Message}", outcome.IsSuccess ? "succeeded" : "failed", job.Title, outcome.Message);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            var reason = stoppingToken.IsCancellationRequested ? "Stopped: the application is closing." : "Cancelled.";
            job.Finish(JobStatus.Cancelled, reason, clock.GetUtcNow());
            logger.LogInformation("Job cancelled: {Title}", job.Title);
        }
        catch (Exception ex)
        {
            job.Finish(JobStatus.Failed, ex.Message, clock.GetUtcNow());
            logger.LogError(ex, "Job threw: {Title}", job.Title);
        }
    }

    /// <summary>Synchronous, so progress values are applied in the order they were reported.</summary>
    private sealed class JobProgressReporter(JobEntry job) : IProgress<JobProgress>
    {
        public void Report(JobProgress value) => job.ReportProgress(value);
    }
}
