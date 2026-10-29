using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Stockhog.Application.Abstractions.Jobs;
using Stockhog.Application.Abstractions.Messaging;
using Stockhog.Application.Abstractions.Results;
using Stockhog.Application.Backtesting;
using Stockhog.Application.Backtesting.Strategies;
using Stockhog.Application.Optimization;
using Stockhog.Application.Optimization.CheckOptimization;
using Stockhog.Application.Optimization.StartOptimization;
using Stockhog.Domain.Backtesting;
using Stockhog.Domain.Optimization;
using Stockhog.Presentation.Backtesting;
using Stockhog.Presentation.Localization;
using Stockhog.Presentation.Settings;
using Stockhog.Presentation.Shell;
using Stockhog.Presentation.Strategies;
using Stockhog.Wpf.Controls.Charts;
using Microsoft.Extensions.Logging;

namespace Stockhog.Presentation.Optimization;

/// <summary>
/// Grid-search optimization: choose which parameters to vary, start it as a background job, watch the trials
/// complete, and inspect the results as a table and a heatmap of scores over two parameters.
/// </summary>
public sealed partial class OptimizationViewModel : PageViewModel, IRecipient<OptimizationsChangedMessage>, IRecipient<OptimizeStrategyMessage>
{
    private readonly IDispatcher _dispatcher;
    private readonly IMessenger _messenger;
    private readonly IJobQueue _jobs;
    private readonly IJobMonitor _monitor;

    public OptimizationViewModel(
        IDispatcher dispatcher,
        IMessenger messenger,
        IJobQueue jobs,
        IJobMonitor monitor,
        StatusViewModel status,
        UserSettings settings,
        AppEnvironmentInfo environment,
        ILogger<OptimizationViewModel> logger)
        : base(status, logger)
    {
        _dispatcher = dispatcher;
        _messenger = messenger;
        _jobs = jobs;
        _monitor = monitor;
        _initialCapital = settings.DefaultInitialCapital;
        _symbol = settings.DefaultSymbols.Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "AAPL";
        _maxParallelTrials = Math.Clamp(environment.MaxParallelJobs, 1, OptimizationRun.MaxParallelTrialsLimit);
        messenger.RegisterAll(this);
    }

    // Form
    [ObservableProperty] private string _symbol;
    [ObservableProperty] private DateTime? _startDate = DateTime.Today.AddYears(-1);
    [ObservableProperty] private DateTime? _endDate = DateTime.Today.AddDays(-1);
    [ObservableProperty] private decimal _initialCapital;
    [ObservableProperty] private IReadOnlyList<StrategyDescriptor> _strategies = [];
    [ObservableProperty] private StrategyDescriptor? _selectedStrategy;
    [ObservableProperty] private OptimizationObjective _objective = OptimizationObjective.SharpeRatio;
    [ObservableProperty] private int _maxParallelTrials;

    public IReadOnlyList<OptimizationObjective> Objectives { get; } = Enum.GetValues<OptimizationObjective>();

    public IReadOnlyList<int> ParallelTrialOptions { get; } = Enumerable.Range(1, OptimizationRun.MaxParallelTrialsLimit).ToList();

    public ObservableCollection<OptimizationParameterInput> Parameters { get; } = [];

    /// <summary>Grid size for the current inputs, e.g. "25 combinations (max 400)".</summary>
    public string CombinationsText
    {
        get
        {
            var varied = Parameters.Where(p => p.IsVaried).ToList();
            if (varied.Count == 0)
                return Loc.Get("Optimization_SelectVaried");

            var count = varied.Aggregate(1L, (total, p) => total * p.Count);
            return Loc.Format("Optimization_Combinations", count, ParameterGrid.MaxCombinations);
        }
    }

    // Results
    [ObservableProperty] private IReadOnlyList<OptimizationSummary> _optimizations = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CancelOptimizationCommand))]
    private OptimizationSummary? _selectedOptimization;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ApplyBestCommand), nameof(OpenTrialCommand), nameof(CheckCommand))]
    private OptimizationDetails? _details;

    [ObservableProperty] private IReadOnlyList<TrialResult> _trials = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(OpenTrialCommand))]
    private TrialResult? _selectedTrial;

    [ObservableProperty] private IReadOnlyList<OptimizationAxis> _axes = [];
    [ObservableProperty] private OptimizationAxis? _xAxis;
    [ObservableProperty] private OptimizationAxis? _yAxis;
    [ObservableProperty] private HeatmapData? _heatmap;
    [ObservableProperty] private string? _bestText;

    public bool HasDetails => Details is not null;

    // Check on another period
    [ObservableProperty] private DateTime? _checkStartDate;
    [ObservableProperty] private DateTime? _checkEndDate;
    [ObservableProperty] private int _checkTop = QueueOptimizationCheckCommand.DefaultTop;
    [ObservableProperty] private string? _checkText;

    public IReadOnlyList<int> CheckTopOptions { get; } = [1, 3, 5, 10, OptimizationRun.MaxCheckedTrials];

    protected override async Task LoadAsync()
    {
        var strategies = await _dispatcher.SendAsync(new GetStrategiesQuery());
        if (strategies.IsFailure)
        {
            Status.Show(strategies.Error);
            return;
        }

        Strategies = strategies.Value;
        SelectedStrategy ??= Strategies.FirstOrDefault(s => s.Name == _requestedStrategy) ?? Strategies.FirstOrDefault();
        await RefreshAsync(CancellationToken.None);
    }

    /// <summary>Name asked for by another page before the strategies were loaded.</summary>
    private string? _requestedStrategy;

    /// <summary>Another page (e.g. Strategies) asks to optimize a strategy: select it, with its suggested ranges.</summary>
    public void Receive(OptimizeStrategyMessage message)
    {
        if (Strategies.FirstOrDefault(s => s.Name == message.StrategyName) is { } strategy)
            SelectedStrategy = strategy;
        else
            _requestedStrategy = message.StrategyName;
    }

    partial void OnSelectedStrategyChanged(StrategyDescriptor? value)
    {
        foreach (var input in Parameters)
            input.PropertyChanged -= OnParameterChanged;
        Parameters.Clear();

        foreach (var parameter in value?.Parameters ?? [])
        {
            var input = new OptimizationParameterInput(value!.Name, parameter);
            input.PropertyChanged += OnParameterChanged;
            Parameters.Add(input);
        }

        // Vary the first parameter by default, so the form starts out valid.
        if (Parameters.FirstOrDefault() is { } first)
            first.IsVaried = true;
        OnPropertyChanged(nameof(CombinationsText));
    }

    private void OnParameterChanged(object? sender, PropertyChangedEventArgs e) => OnPropertyChanged(nameof(CombinationsText));

    protected override void OnBusyChanged() => StartCommand.NotifyCanExecuteChanged();

    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task StartAsync(CancellationToken cancellationToken)
    {
        if (SelectedStrategy is not { } strategy || StartDate is not { } start || EndDate is not { } end)
        {
            Status.Show(Loc.Get("Status_FixInputs"));
            return;
        }

        if (Parameters.FirstOrDefault(p => p.Error is not null) is { } invalid)
        {
            Status.Show($"{invalid.DisplayName}: {invalid.Error}");
            return;
        }

        var ranges = Parameters.Select(p => p.TryGetRange(out var range) ? range : null).OfType<ParameterRangeInput>().ToList();
        var fixedValues = Parameters.Where(p => !p.IsVaried).ToDictionary(p => p.Name, p => p.Value);
        var command = new StartOptimizationCommand(
            strategy.Name,
            Symbol.Trim().ToUpperInvariant(),
            DateOnly.FromDateTime(start),
            DateOnly.FromDateTime(end),
            InitialCapital,
            fixedValues,
            ranges,
            Objective,
            MaxParallelTrials);

        var started = await _dispatcher.SendAsync(command, cancellationToken);
        if (started.IsFailure)
        {
            Status.Show(Describe(started.Error));
            return;
        }

        Status.Show(started.Value.SkippedCombinations == 0
            ? Loc.Format("Status_OptimizationStarted", started.Value.Trials)
            : Loc.Format("Status_OptimizationStartedSkipped", started.Value.Trials, started.Value.SkippedCombinations));

        await RefreshAsync(cancellationToken);
        SelectedOptimization = Optimizations.FirstOrDefault(o => o.Id == started.Value.OptimizationId);
    }

    private bool CanStart() => !IsBusy;

    private bool CanCancelOptimization() => SelectedOptimization is { IsActive: true };

    [RelayCommand(CanExecute = nameof(CanCancelOptimization))]
    private void CancelOptimization()
    {
        var key = $"optimization:{SelectedOptimization!.Id}";
        if (_monitor.GetJobs().FirstOrDefault(j => j.IsActive && j.Key == key) is { } job)
            _jobs.TryCancel(job.Id);
    }

    public async void Receive(OptimizationsChangedMessage message) => await RefreshAsync(CancellationToken.None);

    [RelayCommand]
    private async Task RefreshAsync(CancellationToken cancellationToken)
    {
        var list = await _dispatcher.SendAsync(new GetOptimizationsQuery(), cancellationToken);
        if (list.IsFailure)
        {
            Status.Show(list.Error);
            return;
        }

        var previous = SelectedOptimization;
        Optimizations = list.Value;
        SelectedOptimization = Optimizations.FirstOrDefault(o => o.Id == previous?.Id) ?? Optimizations.FirstOrDefault();

        // An unchanged summary raises no change, but its details may have (e.g. a finished check): reload them.
        if (Equals(previous, SelectedOptimization))
            await LoadDetailsAsync(SelectedOptimization);
    }

    partial void OnSelectedOptimizationChanged(OptimizationSummary? value) => _ = LoadDetailsAsync(value);

    private async Task LoadDetailsAsync(OptimizationSummary? summary)
    {
        if (summary is null)
        {
            Details = null;
            return;
        }

        var details = await _dispatcher.SendAsync(new GetOptimizationQuery(summary.Id));
        if (details.IsFailure)
        {
            Status.Show(details.Error);
            return;
        }

        // A newer selection may have loaded meanwhile.
        if (SelectedOptimization?.Id == summary.Id)
            Details = details.Value;
    }

    partial void OnDetailsChanged(OptimizationDetails? value)
    {
        OnPropertyChanged(nameof(HasDetails));
        var selectedTrial = SelectedTrial?.Index;
        Trials = value?.Trials
            .OrderByDescending(t => t.Score.HasValue)
            .ThenByDescending(t => t.Score)
            .ThenBy(t => t.Index)
            .ToList() ?? [];
        SelectedTrial = Trials.FirstOrDefault(t => t.Index == selectedTrial);

        // Keep the chosen axes while the same optimization refreshes.
        var x = XAxis?.Name;
        var y = YAxis?.Name;
        Axes = value?.Axes.Select(a => a with { DisplayName = StrategyTexts.ParameterName(value.Summary.StrategyName, a.Name, a.DisplayName) }).ToList() ?? [];
        XAxis = Axes.FirstOrDefault(a => a.Name == x) ?? Axes.FirstOrDefault();
        YAxis = Axes.FirstOrDefault(a => a.Name == y && a.Name != XAxis?.Name) ?? Axes.FirstOrDefault(a => a != XAxis);

        BestText = value?.Best is { } best
            ? Loc.Format("Optimization_BestTrial", best.Index, best.PointText, best.Score!.Value.ToString("0.####", Loc.Instance.Culture))
            : null;
        UpdateHeatmap();
        UpdateCheck(value);
    }

    /// <summary>Shortest period after the optimization worth suggesting for a check; a few days say little.</summary>
    private const int MinCheckDays = 30;

    /// <summary>
    /// Shows the latest check, and suggests a period for a new one when another optimization is selected: from the
    /// day after the optimization's period to yesterday, or, when that is under a month, an equally long period
    /// before it.
    /// </summary>
    private void UpdateCheck(OptimizationDetails? details)
    {
        CheckText = details is { CheckStart: { } checkStart, CheckEnd: { } checkEnd }
            ? Loc.Format("Optimization_CheckDone", checkStart.ToString("d", Loc.Instance.Culture), checkEnd.ToString("d", Loc.Instance.Culture))
            : null;

        if (details is null || details.Summary.Id == _checkSuggestedFor)
            return;

        _checkSuggestedFor = details.Summary.Id;
        var summary = details.Summary;
        var yesterday = DateOnly.FromDateTime(DateTime.Today.AddDays(-1));
        var (start, end) = details is { CheckStart: { } s, CheckEnd: { } e }
            ? (s, e)
            : yesterday.DayNumber - summary.End.DayNumber >= MinCheckDays
                ? (summary.End.AddDays(1), yesterday)
                : (summary.Start.AddDays(-(summary.End.DayNumber - summary.Start.DayNumber + 1)), summary.Start.AddDays(-1));

        CheckStartDate = start.ToDateTime(TimeOnly.MinValue);
        CheckEndDate = end.ToDateTime(TimeOnly.MinValue);
    }

    /// <summary>Optimization the check period was last suggested for, so a refresh keeps the user's dates.</summary>
    private string? _checkSuggestedFor;

    private bool CanCheck() => Details is { Best: not null, Summary.IsActive: false };

    /// <summary>Backtests the best trials again on the check period, as one background job.</summary>
    [RelayCommand(CanExecute = nameof(CanCheck))]
    private async Task CheckAsync(CancellationToken cancellationToken)
    {
        if (CheckStartDate is not { } start || CheckEndDate is not { } end)
        {
            Status.Show(Loc.Get("Status_FixInputs"));
            return;
        }

        var queued = await _dispatcher.SendAsync(
            new QueueOptimizationCheckCommand(Details!.Summary.Id, DateOnly.FromDateTime(start), DateOnly.FromDateTime(end), CheckTop),
            cancellationToken);

        Status.Show(queued.IsSuccess ? Loc.Format("Status_CheckStarted", queued.Value.Trials) : Describe(queued.Error));
    }

    partial void OnXAxisChanged(OptimizationAxis? value) => UpdateHeatmap();

    partial void OnYAxisChanged(OptimizationAxis? value) => UpdateHeatmap();

    private void UpdateHeatmap()
    {
        if (Details is not { } details || XAxis is null || XAxis == YAxis)
        {
            Heatmap = null;
            return;
        }

        var map = details.Heatmap(XAxis.Name, YAxis?.Name);
        Heatmap = new HeatmapData(
            XAxis.DisplayName,
            YAxis?.DisplayName ?? string.Empty,
            map.X.Labels,
            map.Y?.Labels ?? [string.Empty],
            map.Scores,
            map.Best,
            "0.##");
    }

    private bool HasBest() => Details?.Best is not null;

    /// <summary>Copies the best parameters to the backtest form, to run or refine them there.</summary>
    [RelayCommand(CanExecute = nameof(HasBest))]
    private void ApplyBest()
    {
        var details = Details!;
        var summary = details.Summary;
        _messenger.Send(new NavigateMessage(typeof(BacktestViewModel)));
        _messenger.Send(new ApplyBacktestSetupMessage(
            summary.StrategyName, summary.Symbol, summary.Start, summary.End, details.InitialCapital, details.Best!.Parameters));
        Status.Show(Loc.Get("Status_BestApplied"));
    }

    private bool CanOpenTrial() => SelectedTrial is { Status: TrialStatus.Completed, RunId: not null };

    [RelayCommand(CanExecute = nameof(CanOpenTrial))]
    private void OpenTrial()
    {
        var summary = Details!.Summary;
        var trial = SelectedTrial!;
        var run = new BacktestRunSummary(
            trial.RunId!, summary.Symbol, summary.StrategyName, summary.Start, summary.End, Details.InitialCapital,
            BacktestStatus.Completed, 100, summary.CreatedAt, null, null, summary.Id);

        _messenger.Send(new NavigateMessage(typeof(BacktestViewModel)));
        _messenger.Send(new OpenBacktestRunMessage(run));
    }

    private static string Describe(Error error) =>
        error.Type == ErrorType.Validation ? error.Message : Loc.Format("Status_Failed", error.Message);
}
