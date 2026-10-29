using System.Collections;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Stockhog.Application.Abstractions.Messaging;
using Stockhog.Application.Backtesting;
using Stockhog.Application.Backtesting.CompareBacktests;
using Stockhog.Application.Backtesting.GetBacktestHistory;
using Stockhog.Presentation.Localization;
using Stockhog.Presentation.Shell;
using Stockhog.Wpf.Controls.Charts;
using Microsoft.Extensions.Logging;

namespace Stockhog.Presentation.Backtesting;

/// <summary>
/// Stored runs: open one on the backtest page, or compare several side by side.
/// </summary>
public sealed partial class HistoryViewModel : PageViewModel, IRecipient<BacktestRunsChangedMessage>
{
    private readonly IDispatcher _dispatcher;
    private readonly IMessenger _messenger;

    public HistoryViewModel(IDispatcher dispatcher, IMessenger messenger, StatusViewModel status, ILogger<HistoryViewModel> logger)
        : base(status, logger)
    {
        _dispatcher = dispatcher;
        _messenger = messenger;
        messenger.RegisterAll(this);
    }

    [ObservableProperty] private IReadOnlyList<BacktestRunSummary> _runs = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(OpenRunCommand), nameof(AnalyzeRunCommand))]
    private BacktestRunSummary? _selectedRun;

    [ObservableProperty] private IReadOnlyList<ComparedRun> _comparedRuns = [];
    [ObservableProperty] private IReadOnlyList<ChartSeries> _comparisonSeries = [];

    public bool HasComparison => ComparedRuns.Count > 0;

    partial void OnComparedRunsChanged(IReadOnlyList<ComparedRun> value) => OnPropertyChanged(nameof(HasComparison));

    protected override Task LoadAsync() => RefreshAsync(CancellationToken.None);

    public async void Receive(BacktestRunsChangedMessage message) => await RefreshAsync(CancellationToken.None);

    [RelayCommand]
    private async Task RefreshAsync(CancellationToken cancellationToken)
    {
        var history = await _dispatcher.SendAsync(new GetBacktestHistoryQuery(), cancellationToken);
        if (history.IsFailure)
        {
            Status.Show(history.Error);
            return;
        }

        var selectedId = SelectedRun?.RunId;
        Runs = history.Value;
        SelectedRun = Runs.FirstOrDefault(r => r.RunId == selectedId);
    }

    private bool CanOpenRun() => SelectedRun is { HasReport: true };

    [RelayCommand(CanExecute = nameof(CanOpenRun))]
    private void OpenRun()
    {
        _messenger.Send(new NavigateMessage(typeof(BacktestViewModel)));
        _messenger.Send(new OpenBacktestRunMessage(SelectedRun!));
    }

    [RelayCommand(CanExecute = nameof(CanOpenRun))]
    private void AnalyzeRun()
    {
        _messenger.Send(new NavigateMessage(typeof(Analytics.AnalysisViewModel)));
        _messenger.Send(new OpenAnalysisMessage(SelectedRun!));
    }

    /// <param name="selectedRows">The history grid's selected items.</param>
    [RelayCommand]
    private async Task CompareAsync(IList? selectedRows, CancellationToken cancellationToken)
    {
        var runs = (selectedRows ?? Array.Empty<object>()).OfType<BacktestRunSummary>()
            .Where(r => r.HasReport)
            .Select(r => new BacktestRunReference(r.Symbol, r.RunId))
            .ToList();

        await RunOperationAsync(Loc.Format("Status_Comparing", runs.Count), async _ =>
        {
            var comparison = await _dispatcher.SendAsync(new CompareBacktestsQuery(runs), cancellationToken);
            if (comparison.IsFailure)
            {
                Status.Show(comparison.Error);
                return Status.Message;
            }

            ComparedRuns = comparison.Value.Runs;
            ComparisonSeries = comparison.Value.Runs
                .Select(run => new ChartSeries(
                    $"{run.Symbol} · {run.RunId[..Math.Min(15, run.RunId.Length)]}",
                    run.CumulativeReturn.Select(p => new TimeValue(p.Time, (double)p.ReturnPercent)).ToList()))
                .ToList();
            return Loc.Format("Status_Compared", runs.Count);
        });
    }
}
