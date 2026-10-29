using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Stockhog.Presentation.Common;
using Stockhog.Presentation.Shell;
using Microsoft.Extensions.Logging;

namespace Stockhog.Presentation.Logging;

/// <summary>
/// Live view of the application log, newest first, refreshed at most a few times per second.
/// </summary>
public sealed partial class LogsViewModel : PageViewModel
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromMilliseconds(300);

    private readonly LogFeed _feed;
    private readonly ThrottledAction _refresh;

    public LogsViewModel(LogFeed feed, StatusViewModel status, ILogger<LogsViewModel> logger) : base(status, logger)
    {
        _feed = feed;
        _refresh = new ThrottledAction(RefreshInterval, Apply, SynchronizationContext.Current);
        _feed.Appended += (_, _) => _refresh.Request();
    }

    public IReadOnlyList<LogLevel> Levels { get; } = Enum.GetValues<LogLevel>();

    [ObservableProperty] private IReadOnlyList<LogEntry> _entries = [];
    [ObservableProperty] private LogLevel _minimumLevel = LogLevel.Information;

    protected override Task LoadAsync()
    {
        Apply();
        return Task.CompletedTask;
    }

    partial void OnMinimumLevelChanged(LogLevel value) => Apply();

    [RelayCommand]
    private void Clear() => _feed.Clear();

    private void Apply() =>
        Entries = _feed.Snapshot().Where(e => e.Level >= MinimumLevel).Reverse().ToList();
}
