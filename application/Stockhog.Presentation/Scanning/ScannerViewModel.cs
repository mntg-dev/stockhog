using System.Collections;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Stockhog.Application.Abstractions.Jobs;
using Stockhog.Application.Abstractions.Messaging;
using Stockhog.Application.Autopilot;
using Stockhog.Application.Backtesting;
using Stockhog.Application.Backtesting.Strategies;
using Stockhog.Application.Proposals;
using Stockhog.Application.Scanning;
using Stockhog.Application.Scanning.CheckSignals;
using Stockhog.Application.Scanning.StartScan;
using Stockhog.Application.Screening;
using Stockhog.Domain.Backtesting;
using Stockhog.Domain.Scanning;
using Stockhog.Presentation.Autopilot;
using Stockhog.Presentation.Backtesting;
using Stockhog.Presentation.Localization;
using Stockhog.Presentation.Proposals;
using Stockhog.Presentation.Settings;
using Stockhog.Presentation.Shell;
using Microsoft.Extensions.Logging;

namespace Stockhog.Presentation.Scanning;

/// <summary>A strategy the scan can include, as a check box.</summary>
public sealed partial class StrategyOption(StrategyDescriptor strategy) : ObservableObject
{
    public StrategyDescriptor Strategy { get; } = strategy;

    [ObservableProperty] private bool _isSelected = true;
}

/// <summary>
/// The second step of the funnel: every chosen strategy on every chosen symbol (usually the screener's result) over
/// one period, with the checks that weed out lucky or fragile results, then a ranking of what held up.
/// </summary>
public sealed partial class ScannerViewModel : PageViewModel, IRecipient<ScansChangedMessage>, IRecipient<ScanSymbolsMessage>
{
    private static readonly char[] SymbolSeparators = [',', ';', ' '];

    private readonly IDispatcher _dispatcher;
    private readonly IMessenger _messenger;
    private readonly IJobQueue _jobs;
    private readonly IJobMonitor _jobMonitor;

    /// <summary>The screener run the symbol list came from; cleared when the list is edited by hand.</summary>
    private Guid? _screenerRunId;

    private bool _settingSymbols;

    public ScannerViewModel(
        IDispatcher dispatcher,
        IMessenger messenger,
        IJobQueue jobs,
        IJobMonitor jobMonitor,
        StatusViewModel status,
        UserSettings settings,
        ILogger<ScannerViewModel> logger)
        : base(status, logger)
    {
        _dispatcher = dispatcher;
        _messenger = messenger;
        _jobs = jobs;
        _jobMonitor = jobMonitor;
        _initialCapital = settings.DefaultInitialCapital;

        // Two years up to yesterday: each half is a full year for the consistency check.
        _endDate = DateTime.Today.AddDays(-1);
        _startDate = _endDate.Value.AddYears(-2);
        messenger.RegisterAll(this);
    }

    public IReadOnlyList<int> ParallelOptions { get; } = Enumerable.Range(1, ScanRun.MaxParallelLimit).ToList();

    // Scan inputs
    [ObservableProperty] private string _symbolList = string.Empty;
    [ObservableProperty] private IReadOnlyList<StrategyOption> _strategies = [];
    [ObservableProperty] private DateTime? _startDate;
    [ObservableProperty] private DateTime? _endDate;
    [ObservableProperty] private decimal _initialCapital;
    [ObservableProperty] private int _minTrades = ScanThresholds.Default.MinTrades;
    [ObservableProperty] private decimal _maxDrawdownPercent = ScanThresholds.Default.MaxDrawdownPercent;
    [ObservableProperty] private bool _requireBothHalvesProfitable = ScanThresholds.Default.RequireBothHalvesProfitable;
    [ObservableProperty] private int _maxParallel = 3;

    // Results
    [ObservableProperty] private IReadOnlyList<ScanSummary> _scans = [];
    [ObservableProperty] private ScanSummary? _selectedScan;
    [ObservableProperty] private ScanDetails? _details;
    [ObservableProperty] private bool _onlyPassed;
    [ObservableProperty] private IReadOnlyList<ScanCandidateRow> _candidates = [];

    public bool HasCandidates => Candidates.Count > 0;

    /// <summary>E.g. "12 of 180 passed · 200/200 done".</summary>
    public string Summary => Details?.Summary is { } scan
        ? Loc.Format("Scanner_Summary", scan.Passed, scan.Finished, scan.Candidates)
        : string.Empty;

    public bool CanStop => SelectedScan?.Status is ScanStatus.Queued or ScanStatus.Running;

    partial void OnSymbolListChanged(string value)
    {
        if (!_settingSymbols)
            _screenerRunId = null;
    }

    partial void OnCandidatesChanged(IReadOnlyList<ScanCandidateRow> value) => OnPropertyChanged(nameof(HasCandidates));

    partial void OnDetailsChanged(ScanDetails? value)
    {
        OnPropertyChanged(nameof(Summary));
        ApplyFilter();
    }

    partial void OnOnlyPassedChanged(bool value) => ApplyFilter();

    partial void OnSelectedScanChanged(ScanSummary? value)
    {
        OnPropertyChanged(nameof(CanStop));
        _ = LoadDetailsAsync(value, CancellationToken.None);
    }

    protected override async Task LoadAsync()
    {
        var strategies = await _dispatcher.SendAsync(new GetStrategiesQuery());
        if (strategies.IsFailure)
            Status.Show(strategies.Error);
        else
            Strategies = strategies.Value.Select(s => new StrategyOption(s)).ToList();

        await RefreshAsync(CancellationToken.None);
    }

    public async void Receive(ScansChangedMessage message) => await RefreshAsync(CancellationToken.None);

    public void Receive(ScanSymbolsMessage message) => SetSymbols(message.Symbols, message.ScreenerRunId);

    /// <summary>The best symbols of the newest screener run, as many as a scan takes.</summary>
    [RelayCommand]
    private async Task UseScreenerResultAsync(CancellationToken cancellationToken)
    {
        var result = await _dispatcher.SendAsync(new GetScreenerResultQuery(), cancellationToken);
        if (result.IsFailure)
        {
            Status.Show(result.Error);
            return;
        }

        SetSymbols(result.Value.Symbols.Take(ScanRun.MaxSymbols).Select(s => s.Symbol).ToList(), result.Value.Summary.Id);
    }

    [RelayCommand]
    private async Task StartAsync(CancellationToken cancellationToken)
    {
        if (StartDate is null || EndDate is null)
        {
            Status.Show(Loc.Get("Status_SelectDates"));
            return;
        }

        var command = new StartScanCommand(
            SymbolList.Split(SymbolSeparators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            Strategies.Where(s => s.IsSelected).Select(s => s.Strategy.Name).ToList(),
            DateOnly.FromDateTime(StartDate.Value),
            DateOnly.FromDateTime(EndDate.Value),
            InitialCapital,
            MinTrades,
            MaxDrawdownPercent,
            RequireBothHalvesProfitable,
            MaxParallel,
            _screenerRunId);

        var result = await _dispatcher.SendAsync(command, cancellationToken);
        if (result.IsFailure)
        {
            Status.Show(result.Error);
            return;
        }

        Status.Show(Loc.Format("Status_ScanStarted", result.Value.Candidates));
        await RefreshAsync(cancellationToken);
    }

    /// <summary>What each passed pair of the selected scan wants to do today, as a background job.</summary>
    [RelayCommand]
    private async Task CheckSignalsAsync(CancellationToken cancellationToken)
    {
        if (SelectedScan is not { } scan)
            return;

        var result = await _dispatcher.SendAsync(new CheckScanSignalsCommand(scan.Id), cancellationToken);
        if (result.IsFailure)
            Status.Show(result.Error);
        else
            Status.Show(Loc.Format(result.Value.WasQueued ? "Status_SignalsQueued" : "Status_SignalsAlreadyRunning", result.Value.Candidates));
    }

    /// <summary>Buy proposals from the fresh entries of the selected scan, shown on the Proposals page for approval.</summary>
    [RelayCommand]
    private async Task CreateProposalsAsync(CancellationToken cancellationToken)
    {
        if (SelectedScan is not { } scan)
            return;

        var created = await _dispatcher.SendAsync(new CreateProposalsCommand(scan.Id), cancellationToken);
        if (created.IsFailure)
        {
            Status.Show(created.Error);
            return;
        }

        var skipped = string.Join("; ", created.Value.Skipped.Select(s => $"{s.Symbol}: {s.Reason}"));
        Status.Show(created.Value.Skipped.Count == 0
            ? Loc.Format("Status_ProposalsCreated", created.Value.ProposalIds.Count)
            : Loc.Format("Status_ProposalsCreatedSkipped", created.Value.ProposalIds.Count, skipped));
        _messenger.Send(new ProposalsChangedMessage());
        if (created.Value.ProposalIds.Count > 0)
            _messenger.Send(new NavigateMessage(typeof(ProposalsViewModel)));
    }

    /// <summary>Lets the autopilot follow the passed pairs of the selected scan; see the Autopilot page.</summary>
    [RelayCommand]
    private async Task AddToAutopilotAsync(CancellationToken cancellationToken)
    {
        if (SelectedScan is not { } scan)
            return;

        var added = await _dispatcher.SendAsync(new AddToAutopilotCommand(scan.Id), cancellationToken);
        if (added.IsFailure)
        {
            Status.Show(added.Error);
            return;
        }

        Status.Show(Loc.Format("Status_AutopilotAdded", added.Value.Added, added.Value.AlreadyFollowed, added.Value.Excluded));
        _messenger.Send(new AutopilotChangedMessage());
        if (added.Value.Added > 0)
            _messenger.Send(new NavigateMessage(typeof(AutopilotViewModel)));
    }

    /// <summary>Cancels the selected scan's job; candidates already evaluated are kept.</summary>
    [RelayCommand]
    private void Stop()
    {
        if (SelectedScan is not { } scan)
            return;

        var job = _jobMonitor.GetJobs().FirstOrDefault(j => j.IsActive && j.Key == ScanKeys.JobKeyPrefix + scan.Id);
        if (job is not null && _jobs.TryCancel(job.Id))
            Status.Show(Loc.Get("Status_ScanStopping"));
    }

    /// <summary>Lists the scans and keeps the selected one, or selects the newest.</summary>
    [RelayCommand]
    private async Task RefreshAsync(CancellationToken cancellationToken)
    {
        var scans = await _dispatcher.SendAsync(new GetScansQuery(), cancellationToken);
        if (scans.IsFailure)
        {
            Status.Show(scans.Error);
            return;
        }

        var previous = SelectedScan;
        Scans = scans.Value;
        SelectedScan = Scans.FirstOrDefault(s => s.Id == previous?.Id) ?? Scans.FirstOrDefault();

        // A signal check changes the candidates but not the summary, so an equal summary does not reload them by itself.
        if (Equals(previous, SelectedScan))
            await LoadDetailsAsync(SelectedScan, cancellationToken);
    }

    /// <summary>Shows the backtest behind the selected candidate on the Backtest page.</summary>
    [RelayCommand]
    private void OpenSelected(IList? selectedRows)
    {
        var candidate = (selectedRows ?? Array.Empty<object>()).OfType<ScanCandidateRow>().FirstOrDefault(c => c.RunId is not null);
        if (candidate is null || Details is not { } details)
            return;

        var run = new BacktestRunSummary(
            candidate.RunId!,
            candidate.Symbol,
            candidate.StrategyName,
            details.Summary.Start,
            details.Summary.End,
            details.InitialCapital,
            BacktestStatus.Completed,
            100,
            details.Summary.CreatedAt,
            null,
            null);

        _messenger.Send(new NavigateMessage(typeof(BacktestViewModel)));
        _messenger.Send(new OpenBacktestRunMessage(run));
    }

    private void SetSymbols(IReadOnlyList<string> symbols, Guid? screenerRunId)
    {
        _settingSymbols = true;
        SymbolList = string.Join(", ", symbols);
        _settingSymbols = false;
        _screenerRunId = screenerRunId;
    }

    private async Task LoadDetailsAsync(ScanSummary? scan, CancellationToken cancellationToken)
    {
        if (scan is null)
        {
            Details = null;
            return;
        }

        var details = await _dispatcher.SendAsync(new GetScanQuery(scan.Id), cancellationToken);
        if (details.IsFailure)
            Status.Show(details.Error);
        else if (SelectedScan?.Id == scan.Id)
            Details = details.Value;
    }

    private void ApplyFilter() =>
        Candidates = Details is null ? [] : Details.Candidates.Where(c => !OnlyPassed || c.Passed).ToList();
}
