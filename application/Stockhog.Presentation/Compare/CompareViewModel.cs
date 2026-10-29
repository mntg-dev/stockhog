using System.Collections;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Stockhog.Application.Abstractions.Messaging;
using Stockhog.Application.Backtesting;
using Stockhog.Application.Backtesting.CompareBacktests;
using Stockhog.Application.Backtesting.GetRunRanking;
using Stockhog.Application.Backtesting.QueueBacktests;
using Stockhog.Application.Backtesting.Strategies;
using Stockhog.Application.MarketData.QueueMarketDataDownloads;
using Stockhog.Domain.MarketData;
using Stockhog.Presentation.Backtesting;
using Stockhog.Presentation.Localization;
using Stockhog.Presentation.Settings;
using Stockhog.Presentation.Shell;
using Stockhog.Wpf.Controls.Charts;
using Microsoft.Extensions.Logging;

namespace Stockhog.Presentation.Compare;

/// <summary>
/// Many symbols side by side: download data and run one strategy over a symbol list as background jobs, then rank
/// the results by any metric, chart the best ones together, or hand them to the assistant.
/// </summary>
public sealed partial class CompareViewModel : PageViewModel, IRecipient<BacktestRunsChangedMessage>
{
    private static readonly char[] SymbolSeparators = [',', ';', ' '];

    private readonly IDispatcher _dispatcher;
    private readonly IMessenger _messenger;

    public CompareViewModel(IDispatcher dispatcher, IMessenger messenger, StatusViewModel status, UserSettings settings, ILogger<CompareViewModel> logger)
        : base(status, logger)
    {
        _dispatcher = dispatcher;
        _messenger = messenger;
        _symbolList = settings.DefaultSymbols;
        _initialCapital = settings.DefaultInitialCapital;
        messenger.RegisterAll(this);
    }

    // Batch inputs
    [ObservableProperty] private IReadOnlyList<StrategyDescriptor> _strategies = [];
    [ObservableProperty] private StrategyDescriptor? _selectedStrategy;
    [ObservableProperty] private string _symbolList;
    [ObservableProperty] private DateTime? _startDate = DateTime.Today.AddYears(-1);
    [ObservableProperty] private DateTime? _endDate = DateTime.Today;
    [ObservableProperty] private decimal _initialCapital;

    // Ranking filters
    [ObservableProperty] private bool _allStrategies;
    [ObservableProperty] private bool _latestOnly = true;
    [ObservableProperty] private bool _onlyListedSymbols;

    [ObservableProperty] private IReadOnlyList<RankedRun> _runs = [];
    [ObservableProperty] private IReadOnlyList<ChartSeries> _chartSeries = [];

    public bool HasRuns => Runs.Count > 0;

    partial void OnRunsChanged(IReadOnlyList<RankedRun> value) => OnPropertyChanged(nameof(HasRuns));

    partial void OnSelectedStrategyChanged(StrategyDescriptor? value) => _ = RefreshAsync(CancellationToken.None);

    partial void OnAllStrategiesChanged(bool value) => _ = RefreshAsync(CancellationToken.None);

    partial void OnLatestOnlyChanged(bool value) => _ = RefreshAsync(CancellationToken.None);

    partial void OnOnlyListedSymbolsChanged(bool value) => _ = RefreshAsync(CancellationToken.None);

    protected override async Task LoadAsync()
    {
        var strategies = await _dispatcher.SendAsync(new GetStrategiesQuery());
        if (strategies.IsFailure)
        {
            Status.Show(strategies.Error);
            return;
        }

        Strategies = strategies.Value;
        SelectedStrategy ??= Strategies.FirstOrDefault();
        await RefreshAsync(CancellationToken.None);
    }

    public async void Receive(BacktestRunsChangedMessage message) => await RefreshAsync(CancellationToken.None);

    private string[] Symbols() => SymbolList.Split(SymbolSeparators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>Minute data for every symbol (what the built-in strategies trade on), as background jobs.</summary>
    [RelayCommand]
    private async Task DownloadDataAsync(CancellationToken cancellationToken)
    {
        if (!TryGetPeriod(out var start, out var end))
            return;

        var result = await _dispatcher.SendAsync(new QueueMarketDataDownloadsCommand(Symbols(), Timeframe.Minute, start, end), cancellationToken);
        if (result.IsFailure)
            Status.Show(result.Error);
        else if (result.Value.AlreadyQueued.Count == 0)
            Status.Show(Loc.Format("Status_QueuedDownloads", result.Value.JobIds.Count));
        else
            Status.Show(Loc.Format("Status_QueuedDownloadsSkipped", result.Value.JobIds.Count, string.Join(", ", result.Value.AlreadyQueued)));
    }

    /// <summary>One run per symbol with the strategy's default parameters; the Backtest page runs custom ones.</summary>
    [RelayCommand]
    private async Task RunBacktestsAsync(CancellationToken cancellationToken)
    {
        if (SelectedStrategy is not { } strategy || !TryGetPeriod(out var start, out var end))
            return;

        var result = await _dispatcher.SendAsync(
            new QueueBacktestsCommand(strategy.Name, Symbols(), start, end, InitialCapital, new Dictionary<string, string>()), cancellationToken);

        if (result.IsFailure)
            Status.Show(result.Error);
        else if (result.Value.AlreadyQueued.Count == 0)
            Status.Show(Loc.Format("Status_QueuedBacktests", result.Value.JobIds.Count));
        else
            Status.Show(Loc.Format("Status_QueuedBacktestsSkipped", result.Value.JobIds.Count, string.Join(", ", result.Value.AlreadyQueued)));
    }

    [RelayCommand]
    private async Task RefreshAsync(CancellationToken cancellationToken)
    {
        var query = new GetRunRankingQuery(
            AllStrategies ? null : SelectedStrategy?.Name,
            LatestOnly,
            OnlyListedSymbols ? Symbols() : null);

        var ranking = await _dispatcher.SendAsync(query, cancellationToken);
        if (ranking.IsFailure)
            Status.Show(ranking.Error);
        else
            Runs = ranking.Value;
    }

    /// <param name="selectedRows">The ranking grid's selected items; at most <see cref="CompareBacktestsQuery.MaxRuns"/> are charted.</param>
    [RelayCommand]
    private async Task ChartSelectedAsync(IList? selectedRows, CancellationToken cancellationToken)
    {
        var runs = Selected(selectedRows)
            .Take(CompareBacktestsQuery.MaxRuns)
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

            ChartSeries = comparison.Value.Runs
                .Select(run => new ChartSeries(run.Symbol, run.CumulativeReturn.Select(p => new TimeValue(p.Time, (double)p.ReturnPercent)).ToList()))
                .ToList();
            return Loc.Format("Status_Compared", runs.Count);
        });
    }

    [RelayCommand]
    private void OpenSelected(IList? selectedRows)
    {
        if (Selected(selectedRows).FirstOrDefault() is not { } run)
            return;

        _messenger.Send(new NavigateMessage(typeof(BacktestViewModel)));
        _messenger.Send(new OpenBacktestRunMessage(run.ToSummary()));
    }

    /// <summary>Opens the assistant with a question about the selected runs, or the top of the table when none is selected.</summary>
    [RelayCommand]
    private void AskAssistant(IList? selectedRows)
    {
        var runs = Selected(selectedRows).ToList();
        if (runs.Count == 0)
            runs = Runs.Take(10).ToList();
        if (runs.Count == 0)
            return;

        var symbols = string.Join(", ", runs.Select(r => r.Symbol).Distinct());
        _messenger.Send(new NavigateMessage(typeof(Assistant.AssistantViewModel)));
        _messenger.Send(new AskAssistantMessage(Loc.Format("Compare_AssistantPrompt", symbols)));
    }

    private static IEnumerable<RankedRun> Selected(IList? selectedRows) => (selectedRows ?? Array.Empty<object>()).OfType<RankedRun>();

    private bool TryGetPeriod(out DateOnly start, out DateOnly end)
    {
        start = end = default;
        if (StartDate is null || EndDate is null)
        {
            Status.Show(Loc.Get("Status_SelectDates"));
            return false;
        }

        start = DateOnly.FromDateTime(StartDate.Value);
        end = DateOnly.FromDateTime(EndDate.Value);
        return true;
    }
}
