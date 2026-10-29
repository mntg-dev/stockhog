using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Stockhog.Application.Abstractions.Messaging;
using Stockhog.Application.Analytics;
using Stockhog.Application.Backtesting;
using Stockhog.Application.Backtesting.GetBacktestHistory;
using Stockhog.Presentation.Localization;
using Stockhog.Presentation.Shell;
using Stockhog.Wpf.Controls.Charts;
using Microsoft.Extensions.Logging;

namespace Stockhog.Presentation.Analytics;

/// <summary>
/// Risk analysis of a finished run: Monte Carlo resampling of its trades (computed in parallel on all cores)
/// and its deepest drawdown periods.
/// </summary>
public sealed partial class AnalysisViewModel : PageViewModel, IRecipient<OpenAnalysisMessage>, IRecipient<BacktestRunsChangedMessage>
{
    private readonly IDispatcher _dispatcher;

    public AnalysisViewModel(IDispatcher dispatcher, IMessenger messenger, StatusViewModel status, ILogger<AnalysisViewModel> logger)
        : base(status, logger)
    {
        _dispatcher = dispatcher;
        messenger.RegisterAll(this);
    }

    // Inputs
    [ObservableProperty] private IReadOnlyList<BacktestRunSummary> _runs = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AnalyzeCommand))]
    private BacktestRunSummary? _selectedRun;

    [ObservableProperty] private int _simulations = 10_000;
    [ObservableProperty] private MonteCarloMethod _method = MonteCarloMethod.Shuffle;
    [ObservableProperty] private int _seed = 42;

    public IReadOnlyList<int> SimulationOptions { get; } = [1_000, 10_000, 50_000, 100_000];

    public IReadOnlyList<MonteCarloMethod> Methods { get; } = Enum.GetValues<MonteCarloMethod>();

    // Results
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasResults))]
    private MonteCarloResult? _monteCarlo;

    [ObservableProperty] private IReadOnlyList<PercentileRow> _percentiles = [];
    [ObservableProperty] private IReadOnlyList<HistogramBar> _drawdownDistribution = [];
    [ObservableProperty] private double? _historicalDrawdown;
    [ObservableProperty] private IReadOnlyList<XYSeries> _paths = [];
    [ObservableProperty] private string? _computeInfo;
    [ObservableProperty] private DrawdownAnalysis? _drawdowns;

    public bool HasResults => MonteCarlo is not null;

    protected override Task LoadAsync() => RefreshRunsAsync();

    public async void Receive(BacktestRunsChangedMessage message) => await RefreshRunsAsync();

    /// <summary>From the history page: select the run and analyze it straight away.</summary>
    public async void Receive(OpenAnalysisMessage message)
    {
        await RefreshRunsAsync();
        SelectedRun = Runs.FirstOrDefault(r => r.RunId == message.Run.RunId) ?? message.Run;
        if (AnalyzeCommand.CanExecute(null))
            await AnalyzeCommand.ExecuteAsync(null);
    }

    private async Task RefreshRunsAsync()
    {
        var history = await _dispatcher.SendAsync(new GetBacktestHistoryQuery());
        if (history.IsFailure)
        {
            Status.Show(history.Error);
            return;
        }

        var selectedId = SelectedRun?.RunId;
        Runs = history.Value.Where(r => r.HasReport).ToList();
        SelectedRun = Runs.FirstOrDefault(r => r.RunId == selectedId) ?? SelectedRun;
    }

    protected override void OnBusyChanged() => AnalyzeCommand.NotifyCanExecuteChanged();

    private bool CanAnalyze() => SelectedRun is not null && !IsBusy;

    [RelayCommand(CanExecute = nameof(CanAnalyze))]
    private async Task AnalyzeAsync(CancellationToken cancellationToken)
    {
        var run = SelectedRun!;
        await RunOperationAsync(Loc.Format("Status_Analyzing", run.RunId, Simulations), async _ =>
        {
            var stopwatch = Stopwatch.StartNew();

            // Both read the same cached report; the simulation itself runs on the thread pool.
            var monteCarloTask = _dispatcher.SendAsync(new SimulateMonteCarloQuery(run.Symbol, run.RunId, Simulations, Method, Seed), cancellationToken);
            var drawdownsTask = _dispatcher.SendAsync(new GetDrawdownAnalysisQuery(run.Symbol, run.RunId), cancellationToken);
            await Task.WhenAll(monteCarloTask, drawdownsTask);
            stopwatch.Stop();

            var monteCarlo = await monteCarloTask;
            var drawdowns = await drawdownsTask;
            Drawdowns = drawdowns.IsSuccess ? drawdowns.Value : null;
            if (monteCarlo.IsFailure)
            {
                MonteCarlo = null;
                return monteCarlo.Error.Message;
            }

            Show(monteCarlo.Value, stopwatch.Elapsed);
            return Loc.Format("Status_Analyzed", run.RunId);
        });
    }

    private void Show(MonteCarloResult result, TimeSpan elapsed)
    {
        MonteCarlo = result;
        Percentiles =
        [
            PercentileRow.From(Loc.Get("Analysis_FinalReturn"), result.FinalReturnPercent),
            PercentileRow.From(Loc.Get("Analysis_MaxDrawdown"), result.MaxDrawdownPercent)
        ];
        DrawdownDistribution = result.DrawdownHistogram.Select(b => new HistogramBar(b.From, b.To, b.Count)).ToList();
        HistoricalDrawdown = result.OriginalMaxDrawdownPercent;

        var simulated = Loc.Get("Analysis_SimulatedPath");
        Paths = result.SamplePaths
            .Select(path => new XYSeries(simulated, path))
            .Append(new XYSeries(Loc.Get("Analysis_HistoricalPath"), result.OriginalPath, IsHighlighted: true))
            .ToList();

        ComputeInfo = Loc.Format("Analysis_ComputeInfo", result.Simulations, result.Trades, (int)elapsed.TotalMilliseconds, Environment.ProcessorCount);
    }
}

/// <summary>One row of the percentile table, e.g. the distribution of final returns.</summary>
public sealed record PercentileRow(string Label, double P5, double P25, double P50, double P75, double P95)
{
    public static PercentileRow From(string label, Application.Analytics.Percentiles p) => new(label, p.P5, p.P25, p.P50, p.P75, p.P95);
}
