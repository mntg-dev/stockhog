using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Stockhog.Application.Abstractions.Jobs;
using Stockhog.Application.Instruments;
using Stockhog.Presentation.Common;
using Stockhog.Presentation.Shell;
using Microsoft.Extensions.Logging;

namespace Stockhog.Presentation.Jobs;

/// <summary>
/// Background jobs. Mirrors <see cref="IJobMonitor"/>, whose notifications arrive on worker threads many times per
/// second, through a <see cref="ThrottledAction"/>; tells other pages when jobs that change their data finish.
/// </summary>
public sealed partial class JobsViewModel : PageViewModel
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromMilliseconds(250);

    private readonly IJobQueue _queue;
    private readonly IJobMonitor _monitor;
    private readonly IMessenger _messenger;
    private readonly ThrottledAction _refresh;
    private readonly HashSet<Guid> _seenFinished = [];
    private readonly Dictionary<Guid, int> _optimizationProgress = [];

    public JobsViewModel(IJobQueue queue, IJobMonitor monitor, IMessenger messenger, StatusViewModel status, ILogger<JobsViewModel> logger)
        : base(status, logger)
    {
        _queue = queue;
        _monitor = monitor;
        _messenger = messenger;
        _refresh = new ThrottledAction(RefreshInterval, Apply, SynchronizationContext.Current);
        _monitor.JobsChanged += (_, _) => _refresh.Request();
    }

    [ObservableProperty] private IReadOnlyList<JobSnapshot> _jobs = [];
    [ObservableProperty] private int _activeCount;

    [RelayCommand]
    private void Cancel(Guid jobId) => _queue.TryCancel(jobId);

    [RelayCommand]
    private void ClearFinished() => _queue.ClearFinished();

    private void Apply()
    {
        Jobs = _monitor.GetJobs();
        ActiveCount = Jobs.Count(j => j.IsActive);

        var newlyFinished = Jobs.Where(j => !j.IsActive && _seenFinished.Add(j.Id)).ToList();
        if (newlyFinished.Any(j => IsBacktest(j) || IsOptimization(j)))
            _messenger.Send(new BacktestRunsChangedMessage());
        if (newlyFinished.Any(j => IsInstrumentRefresh(j) && j.Status == JobStatus.Succeeded))
            _messenger.Send(new InstrumentsChangedMessage());
        if (newlyFinished.Any(j => !IsBacktest(j) && !IsOptimization(j) && !IsInstrumentRefresh(j) && j.Status == JobStatus.Succeeded))
            _messenger.Send(new MarketDataChangedMessage());

        // Optimizations save after every trial: tell their page when a job's progress moves, not on every refresh.
        // A check of an optimization on another period runs under its backtest key, so finished backtests count too.
        var optimizationsChanged = newlyFinished.Any(IsBacktest);
        foreach (var job in Jobs.Where(IsOptimization))
        {
            if (!_optimizationProgress.TryGetValue(job.Id, out var seen) || seen != job.Progress || newlyFinished.Contains(job))
                optimizationsChanged = true;
            _optimizationProgress[job.Id] = job.Progress;
        }

        if (optimizationsChanged)
            _messenger.Send(new OptimizationsChangedMessage());
    }

    private static bool IsBacktest(JobSnapshot job) => job.Key?.StartsWith("backtest:", StringComparison.Ordinal) == true;

    private static bool IsOptimization(JobSnapshot job) => job.Key?.StartsWith("optimization:", StringComparison.Ordinal) == true;

    private static bool IsInstrumentRefresh(JobSnapshot job) => job.Key == InstrumentCacheKeys.RefreshJobKey;
}
