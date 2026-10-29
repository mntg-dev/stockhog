using System.Collections.Concurrent;
using System.ComponentModel.DataAnnotations;
using System.Threading.Channels;
using Stockhog.Application.Abstractions.Jobs;
using Microsoft.Extensions.Options;

namespace Stockhog.Infrastructure.Jobs;

public sealed class JobOptions
{
    public const string SectionName = "Jobs";

    /// <summary>Jobs running at the same time; the rest wait in the queue.</summary>
    [Range(1, 16)]
    public int MaxParallelJobs { get; set; } = 3;

    /// <summary>Finished jobs kept for display; older ones are dropped.</summary>
    [Range(1, 1000)]
    public int MaxFinishedJobsKept { get; set; } = 50;
}

/// <summary>
/// Producer side of the job pipeline. <see cref="Enqueue"/> never blocks the caller (UI thread): jobs go into an
/// unbounded channel, and <see cref="JobProcessor"/> limits how many run at once.
/// </summary>
internal sealed class JobQueue(TimeProvider clock, IOptions<JobOptions> options) : IJobQueue, IJobMonitor
{
    private readonly Channel<JobEntry> _channel = Channel.CreateUnbounded<JobEntry>(new UnboundedChannelOptions { SingleReader = true });
    private readonly ConcurrentDictionary<Guid, JobEntry> _jobs = new();

    // Makes "is a job with this key active?" and "add the job" one atomic step.
    private readonly Lock _enqueueLock = new();

    public event EventHandler? JobsChanged;

    internal ChannelReader<JobEntry> Reader => _channel.Reader;

    public JobEnqueueResult Enqueue(string title, JobWork work, string? key = null)
    {
        JobEntry entry;
        lock (_enqueueLock)
        {
            var active = key is null ? null : _jobs.Values.FirstOrDefault(j => j.Key == key && j.Snapshot().IsActive);
            if (active is not null)
                return new JobEnqueueResult(active.Snapshot(), WasQueued: false);

            entry = new JobEntry(Guid.NewGuid(), title, key, work, clock.GetUtcNow(), RaiseChanged);
            _jobs[entry.Id] = entry;
            _channel.Writer.TryWrite(entry);
        }

        PruneFinished();
        RaiseChanged();
        return new JobEnqueueResult(entry.Snapshot(), WasQueued: true);
    }

    public bool TryCancel(Guid jobId) => _jobs.TryGetValue(jobId, out var entry) && entry.Cancel(clock.GetUtcNow());

    public void ClearFinished()
    {
        foreach (var entry in _jobs.Values.Where(j => !j.Snapshot().IsActive))
            _jobs.TryRemove(entry.Id, out _);

        RaiseChanged();
    }

    public IReadOnlyList<JobSnapshot> GetJobs() =>
        _jobs.Values.Select(j => j.Snapshot()).OrderByDescending(j => j.QueuedAt).ToList();

    /// <summary>No more jobs are accepted; used when the application stops.</summary>
    internal void Complete() => _channel.Writer.TryComplete();

    private void PruneFinished()
    {
        var finished = _jobs.Values.Select(j => j.Snapshot()).Where(j => !j.IsActive).OrderByDescending(j => j.FinishedAt).ToList();
        foreach (var old in finished.Skip(options.Value.MaxFinishedJobsKept))
            _jobs.TryRemove(old.Id, out _);
    }

    private void RaiseChanged() => JobsChanged?.Invoke(this, EventArgs.Empty);
}

/// <summary>
/// Mutable state of one job. The processor thread, the UI (cancel) and readers (snapshots) access it
/// concurrently, so every read and transition happens under a lock; change notifications fire outside it.
/// </summary>
internal sealed class JobEntry
{
    private readonly Lock _lock = new();
    private readonly CancellationTokenSource _cancellation = new();
    private readonly Action _changed;

    private JobStatus _status = JobStatus.Queued;
    private int _progress;
    private string? _message;
    private DateTimeOffset? _startedAt;
    private DateTimeOffset? _finishedAt;

    public JobEntry(Guid id, string title, string? key, JobWork work, DateTimeOffset queuedAt, Action changed)
    {
        Id = id;
        Title = title;
        Key = key;
        Work = work;
        QueuedAt = queuedAt;
        _changed = changed;
    }

    public Guid Id { get; }
    public string Title { get; }
    public string? Key { get; }
    public JobWork Work { get; }
    public DateTimeOffset QueuedAt { get; }

    public CancellationToken CancellationToken => _cancellation.Token;

    /// <returns><c>false</c> if the job was cancelled while it waited in the queue.</returns>
    public bool TryStart(DateTimeOffset at) => Transition(() =>
    {
        if (_status != JobStatus.Queued)
            return false;

        _status = JobStatus.Running;
        _startedAt = at;
        return true;
    });

    public void ReportProgress(JobProgress progress) => Transition(() =>
    {
        if (_status != JobStatus.Running)
            return false;

        _progress = Math.Max(_progress, Math.Clamp(progress.Percent, 0, 100));
        _message = progress.Message ?? _message;
        return true;
    });

    public void Finish(JobStatus status, string message, DateTimeOffset at) => Transition(() =>
    {
        if (_status != JobStatus.Running)
            return false;

        _status = status;
        _message = message;
        _finishedAt = at;
        if (status == JobStatus.Succeeded)
            _progress = 100;
        return true;
    });

    /// <summary>
    /// A queued job is cancelled immediately. A running job is signalled and becomes Cancelled when its work stops.
    /// </summary>
    public bool Cancel(DateTimeOffset at)
    {
        var accepted = Transition(() =>
        {
            switch (_status)
            {
                case JobStatus.Queued:
                    _status = JobStatus.Cancelled;
                    _message = "Cancelled before it started.";
                    _finishedAt = at;
                    return true;
                case JobStatus.Running:
                    _message = "Cancelling...";
                    return true;
                default:
                    return false;
            }
        });

        if (accepted)
            _cancellation.Cancel();

        return accepted;
    }

    public JobSnapshot Snapshot()
    {
        lock (_lock)
        {
            return new JobSnapshot(Id, Title, Key, _status, _progress, _message, QueuedAt, _startedAt, _finishedAt);
        }
    }

    private bool Transition(Func<bool> change)
    {
        bool changed;
        lock (_lock)
        {
            changed = change();
        }

        if (changed)
            _changed();

        return changed;
    }
}
