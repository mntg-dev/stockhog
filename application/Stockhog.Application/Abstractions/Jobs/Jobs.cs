using Stockhog.Application.Abstractions.Messaging;

namespace Stockhog.Application.Abstractions.Jobs;

/// <summary>
/// Background work that outlives the UI action that started it: queued, run with bounded parallelism,
/// observable and cancellable.
/// </summary>
public interface IJobQueue
{
    /// <param name="key">Optional identity of the work, e.g. <c>AAPL:Minute</c>. While a job with the same key is
    /// queued or running, the existing job is returned instead of queuing a duplicate.</param>
    JobEnqueueResult Enqueue(string title, JobWork work, string? key = null);

    /// <returns><c>false</c> if the job does not exist or has already finished.</returns>
    bool TryCancel(Guid jobId);

    void ClearFinished();
}

/// <summary>
/// Read side of the queue for the UI. <see cref="JobsChanged"/> is raised on worker threads,
/// possibly many times per second, so subscribers should marshal and throttle.
/// </summary>
public interface IJobMonitor
{
    IReadOnlyList<JobSnapshot> GetJobs();

    event EventHandler? JobsChanged;
}

/// <summary>
/// The work of a job. Return the final message; throw to fail; cancellation is observed through the context token.
/// </summary>
public delegate Task<JobOutcome> JobWork(JobExecutionContext context);

public sealed record JobExecutionContext(IDispatcher Dispatcher, IProgress<JobProgress> Progress, CancellationToken CancellationToken);

public readonly record struct JobProgress(int Percent, string? Message = null);

public sealed record JobOutcome(bool IsSuccess, string Message)
{
    public static JobOutcome Succeeded(string message) => new(true, message);

    public static JobOutcome Failed(string message) => new(false, message);
}

public sealed record JobEnqueueResult(JobSnapshot Job, bool WasQueued);

public enum JobStatus
{
    Queued,
    Running,
    Succeeded,
    Failed,
    Cancelled
}

public sealed record JobSnapshot(
    Guid Id,
    string Title,
    string? Key,
    JobStatus Status,
    int Progress,
    string? Message,
    DateTimeOffset QueuedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? FinishedAt)
{
    public bool IsActive => Status is JobStatus.Queued or JobStatus.Running;

    public TimeSpan? Duration => StartedAt is { } started ? (FinishedAt ?? DateTimeOffset.UtcNow) - started : null;
}
