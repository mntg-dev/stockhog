using Stockhog.Application.Abstractions.Jobs;
using Stockhog.Application.Abstractions.Messaging;
using Stockhog.Application.Abstractions.Results;
using Stockhog.Infrastructure.Jobs;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Stockhog.Infrastructure.Tests.Jobs;

/// <summary>
/// The real queue and processor running on background threads.
/// </summary>
public sealed class JobProcessorTests : IAsyncDisposable
{
    private JobQueue _queue = null!;
    private JobProcessor _processor = null!;

    [Fact]
    public async Task Runs_at_most_the_configured_number_of_jobs_at_once()
    {
        await StartAsync(maxParallelJobs: 2);
        var running = 0;
        var maxRunning = 0;

        for (var i = 0; i < 6; i++)
        {
            _queue.Enqueue($"job {i}", async context =>
            {
                var now = Interlocked.Increment(ref running);
                InterlockedMax(ref maxRunning, now);
                await Task.Delay(100, context.CancellationToken);
                Interlocked.Decrement(ref running);
                return JobOutcome.Succeeded("ok");
            });
        }

        await WaitUntilAsync(() => _queue.GetJobs().All(j => !j.IsActive));

        Assert.Equal(2, maxRunning);
        Assert.All(_queue.GetJobs(), j => Assert.Equal(JobStatus.Succeeded, j.Status));
    }

    [Fact]
    public async Task Progress_and_outcomes_are_reflected_in_snapshots()
    {
        await StartAsync();
        var gate = new TaskCompletionSource();

        var reporting = _queue.Enqueue("reports progress", async context =>
        {
            context.Progress.Report(new JobProgress(40, "halfway-ish"));
            await gate.Task;
            return JobOutcome.Succeeded("done");
        }).Job;
        var failing = _queue.Enqueue("fails", _ => Task.FromResult(JobOutcome.Failed("no data"))).Job;
        var throwing = _queue.Enqueue("throws", _ => throw new InvalidOperationException("boom")).Job;

        await WaitUntilAsync(() => Find(reporting.Id).Progress == 40);
        Assert.Equal((JobStatus.Running, "halfway-ish"), (Find(reporting.Id).Status, Find(reporting.Id).Message));

        gate.SetResult();
        await WaitUntilAsync(() => _queue.GetJobs().All(j => !j.IsActive));

        Assert.Equal((JobStatus.Succeeded, 100, "done"), (Find(reporting.Id).Status, Find(reporting.Id).Progress, Find(reporting.Id).Message));
        Assert.Equal((JobStatus.Failed, "no data"), (Find(failing.Id).Status, Find(failing.Id).Message));
        Assert.Equal((JobStatus.Failed, "boom"), (Find(throwing.Id).Status, Find(throwing.Id).Message));
    }

    [Fact]
    public async Task Running_job_can_be_cancelled()
    {
        await StartAsync();
        var job = _queue.Enqueue("waits forever", async context =>
        {
            await Task.Delay(Timeout.Infinite, context.CancellationToken);
            return JobOutcome.Succeeded("unreachable");
        }).Job;
        await WaitUntilAsync(() => Find(job.Id).Status == JobStatus.Running);

        Assert.True(_queue.TryCancel(job.Id));

        await WaitUntilAsync(() => Find(job.Id).Status == JobStatus.Cancelled);
        Assert.False(_queue.TryCancel(job.Id));
    }

    [Fact]
    public async Task Queued_job_cancelled_before_it_starts_never_runs()
    {
        await StartAsync(maxParallelJobs: 1);
        var gate = new TaskCompletionSource();
        var secondRan = false;

        var first = _queue.Enqueue("blocks the only slot", async _ => { await gate.Task; return JobOutcome.Succeeded("ok"); }).Job;
        var second = _queue.Enqueue("waits in the queue", _ => { secondRan = true; return Task.FromResult(JobOutcome.Succeeded("ok")); }).Job;

        Assert.True(_queue.TryCancel(second.Id));
        Assert.Equal(JobStatus.Cancelled, Find(second.Id).Status);

        gate.SetResult();
        await WaitUntilAsync(() => Find(first.Id).Status == JobStatus.Succeeded);
        await Task.Delay(100, TestContext.Current.CancellationToken);
        Assert.False(secondRan);
    }

    [Fact]
    public async Task Same_key_is_not_queued_twice_while_active()
    {
        await StartAsync();
        var gate = new TaskCompletionSource();
        JobWork work = async _ => { await gate.Task; return JobOutcome.Succeeded("ok"); };

        var first = _queue.Enqueue("first", work, key: "AAPL:Minute");
        var duplicate = _queue.Enqueue("duplicate", work, key: "AAPL:Minute");

        Assert.True(first.WasQueued);
        Assert.False(duplicate.WasQueued);
        Assert.Equal(first.Job.Id, duplicate.Job.Id);

        gate.SetResult();
        await WaitUntilAsync(() => !Find(first.Job.Id).IsActive);
        Assert.True(_queue.Enqueue("again after it finished", work, key: "AAPL:Minute").WasQueued);
    }

    [Fact]
    public async Task Stopping_the_processor_cancels_running_jobs()
    {
        await StartAsync();
        var job = _queue.Enqueue("long", async context =>
        {
            await Task.Delay(Timeout.Infinite, context.CancellationToken);
            return JobOutcome.Succeeded("unreachable");
        }).Job;
        await WaitUntilAsync(() => Find(job.Id).Status == JobStatus.Running);

        await _processor.StopAsync(TestContext.Current.CancellationToken);

        Assert.Equal(JobStatus.Cancelled, Find(job.Id).Status);
        Assert.StartsWith("Stopped", Find(job.Id).Message);
    }

    [Fact]
    public async Task Changes_are_announced()
    {
        await StartAsync();
        var notifications = 0;
        _queue.JobsChanged += (_, _) => Interlocked.Increment(ref notifications);

        var job = _queue.Enqueue("quick", _ => Task.FromResult(JobOutcome.Succeeded("ok"))).Job;
        await WaitUntilAsync(() => Find(job.Id).Status == JobStatus.Succeeded);

        // Enqueued, started, finished.
        Assert.True(notifications >= 3, $"{notifications} notification(s)");
    }

    public async ValueTask DisposeAsync()
    {
        await _processor.StopAsync(CancellationToken.None);
        _processor.Dispose();
    }

    private async Task StartAsync(int maxParallelJobs = 3)
    {
        var options = Options.Create(new JobOptions { MaxParallelJobs = maxParallelJobs });
        _queue = new JobQueue(TimeProvider.System, options);
        _processor = new JobProcessor(_queue, new UnusedDispatcher(), options, TimeProvider.System, NullLogger<JobProcessor>.Instance);
        await _processor.StartAsync(TestContext.Current.CancellationToken);
    }

    private JobSnapshot Find(Guid id) => _queue.GetJobs().Single(j => j.Id == id);

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException("Condition was not met within 10 seconds.");

            await Task.Delay(10, TestContext.Current.CancellationToken);
        }
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int current;
        while (value > (current = Volatile.Read(ref target)) && Interlocked.CompareExchange(ref target, value, current) != current)
        {
        }
    }

    private sealed class UnusedDispatcher : IDispatcher
    {
        public Task<Result<TResponse>> SendAsync<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
