using Stockhog.Application.Abstractions.Messaging;
using Stockhog.Application.Backtesting.BackfillMetrics;
using Stockhog.Application.Instruments.QueueInstrumentRefresh;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Stockhog.Infrastructure.Jobs;

/// <summary>
/// At startup, queues maintenance that is due: a refresh of the instrument list when it is missing or out of date,
/// and metrics for runs stored without them. Both run as background jobs, so startup does not wait for them.
/// </summary>
internal sealed class StartupJobScheduler(IDispatcher dispatcher, ILogger<StartupJobScheduler> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var instruments = await dispatcher.SendAsync(new QueueInstrumentRefreshCommand(OnlyIfStale: true), cancellationToken);
        if (instruments.IsFailure)
            logger.LogWarning("Could not schedule the instrument list refresh: {Error}", instruments.Error.Message);
        else if (instruments.Value.WasQueued)
            logger.LogInformation("Instrument list is missing or out of date; refresh queued as job {JobId}", instruments.Value.JobId);

        var metrics = await dispatcher.SendAsync(new QueueMetricsBackfillCommand(), cancellationToken);
        if (metrics.IsFailure)
            logger.LogWarning("Could not schedule the metrics backfill: {Error}", metrics.Error.Message);
        else if (metrics.Value)
            logger.LogInformation("Some completed runs have no metrics; backfill queued");
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
