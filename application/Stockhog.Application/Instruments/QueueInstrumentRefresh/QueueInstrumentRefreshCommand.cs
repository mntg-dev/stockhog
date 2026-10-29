using Stockhog.Application.Abstractions.Jobs;
using Stockhog.Application.Abstractions.Messaging;
using Stockhog.Application.Abstractions.Results;
using Stockhog.Application.Instruments.RefreshInstruments;

namespace Stockhog.Application.Instruments.QueueInstrumentRefresh;

/// <summary>
/// Refreshes the instrument list as a background job.
/// </summary>
/// <param name="OnlyIfStale">For the automatic refresh at startup: queue only when the list is empty or older than
/// <see cref="MaxAge"/>, and only when the source is configured (no failed job for users without keys).</param>
public sealed record QueueInstrumentRefreshCommand(bool OnlyIfStale = false) : ICommand<QueuedInstrumentRefresh>
{
    public static readonly TimeSpan MaxAge = TimeSpan.FromDays(7);
}

/// <param name="JobId">The queued job, or the one already running; <c>null</c> when nothing needed to be done.</param>
public sealed record QueuedInstrumentRefresh(Guid? JobId, bool WasQueued);

internal sealed class QueueInstrumentRefreshCommandHandler(IJobQueue jobs, IInstrumentSource source, IInstrumentStore store, TimeProvider clock)
    : ICommandHandler<QueueInstrumentRefreshCommand, QueuedInstrumentRefresh>
{
    public async Task<Result<QueuedInstrumentRefresh>> HandleAsync(QueueInstrumentRefreshCommand command, CancellationToken cancellationToken)
    {
        if (command.OnlyIfStale)
        {
            if (!source.IsConfigured)
                return new QueuedInstrumentRefresh(null, false);

            var status = await store.GetStatusAsync(cancellationToken).ConfigureAwait(false);
            var isFresh = status.Count > 0 && status.RefreshedAt is { } refreshedAt && clock.GetUtcNow() - refreshedAt < QueueInstrumentRefreshCommand.MaxAge;
            if (isFresh)
                return new QueuedInstrumentRefresh(null, false);
        }
        else if (!source.IsConfigured)
        {
            return InstrumentErrors.SourceNotConfigured;
        }

        var result = jobs.Enqueue("Refresh instrument list", RunAsync, InstrumentCacheKeys.RefreshJobKey);
        return new QueuedInstrumentRefresh(result.Job.Id, result.WasQueued);
    }

    private static async Task<JobOutcome> RunAsync(JobExecutionContext context)
    {
        context.Progress.Report(new JobProgress(10, "Downloading the instrument list"));
        var result = await context.Dispatcher.SendAsync(new RefreshInstrumentsCommand(), context.CancellationToken);
        return result.Match(r => JobOutcome.Succeeded(r.Describe()), error => JobOutcome.Failed(error.Message));
    }
}
