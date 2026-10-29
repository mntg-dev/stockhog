using System.Collections.ObjectModel;
using System.ComponentModel.DataAnnotations;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Stockhog.Application.Abstractions.Messaging;
using Stockhog.Application.Backtesting;
using Stockhog.Application.Backtesting.GetBacktestReport;
using Stockhog.Application.Backtesting.GetLatestBacktestReport;
using Stockhog.Application.Backtesting.QueueBacktests;
using Stockhog.Application.Backtesting.RunBacktest;
using Stockhog.Application.Backtesting.Strategies;
using Stockhog.Application.MarketData.GetPriceBars;
using Stockhog.Domain.MarketData;
using Stockhog.Domain.Trading;
using Stockhog.Presentation.Localization;
using Stockhog.Presentation.Settings;
using Stockhog.Presentation.Shell;
using Stockhog.Wpf.Controls.Charts;
using Microsoft.Extensions.Logging;

namespace Stockhog.Presentation.Backtesting;

/// <summary>
/// Run a strategy (interactively or as background jobs for a symbol list) and inspect one report:
/// key figures, equity with drawdown, price chart with trades, and the trade list.
/// </summary>
public sealed partial class BacktestViewModel : PageViewModel, IRecipient<OpenBacktestRunMessage>, IRecipient<ApplyBacktestSetupMessage>, IRecipient<SelectBacktestStrategyMessage>
{
    private static readonly char[] SymbolSeparators = [',', ';', ' '];

    private readonly IDispatcher _dispatcher;
    private readonly IMessenger _messenger;

    public BacktestViewModel(IDispatcher dispatcher, IMessenger messenger, StatusViewModel status, UserSettings settings, ILogger<BacktestViewModel> logger)
        : base(status, logger)
    {
        _dispatcher = dispatcher;
        _messenger = messenger;
        _symbolList = settings.DefaultSymbols;
        _initialCapital = settings.DefaultInitialCapital;
        messenger.RegisterAll(this);
    }

    // Inputs
    [ObservableProperty]
    [NotifyDataErrorInfo]
    [Required(ErrorMessage = " ")]
    [CustomValidation(typeof(BacktestViewModel), nameof(ValidateSymbol))]
    [NotifyCanExecuteChangedFor(nameof(RunBacktestCommand))]
    private string _symbol = "AAPL";

    [ObservableProperty] private string _symbolList;
    [ObservableProperty] private DateTime? _startDate = DateTime.Today.AddYears(-1);
    [ObservableProperty] private DateTime? _endDate = DateTime.Today;

    [ObservableProperty]
    [NotifyDataErrorInfo]
    [Range(typeof(decimal), "1", "1000000000000", ErrorMessage = " ")]
    [NotifyCanExecuteChangedFor(nameof(RunBacktestCommand), nameof(RunBacktestBatchCommand))]
    private decimal _initialCapital;

    [ObservableProperty] private IReadOnlyList<StrategyDescriptor> _strategies = [];
    [ObservableProperty] private StrategyDescriptor? _selectedStrategy;

    public ObservableCollection<StrategyParameterInput> StrategyParameters { get; } = [];

    // Results
    [ObservableProperty] private string? _reportTitle;
    [ObservableProperty] private string _compoundingReturn = "–";
    [ObservableProperty] private string _maxDrawdown = "–";
    [ObservableProperty] private string _winRate = "–";
    [ObservableProperty] private string _profitFactor = "–";
    [ObservableProperty] private string _sharpeRatio = "–";
    [ObservableProperty] private string _totalTrades = "–";
    [ObservableProperty] private string _averageWin = "–";
    [ObservableProperty] private string _averageLoss = "–";
    [ObservableProperty] private int _maxConsecutiveWins;
    [ObservableProperty] private int _maxConsecutiveLosses;
    [ObservableProperty] private IReadOnlyList<ClosedTrade> _trades = [];
    [ObservableProperty] private IReadOnlyList<TimeValue> _equity = [];
    [ObservableProperty] private IReadOnlyList<PriceBar> _priceBars = [];
    [ObservableProperty] private IReadOnlyList<TradeMarker> _tradeMarkers = [];

    public static ValidationResult? ValidateSymbol(string symbol, ValidationContext _) =>
        Domain.MarketData.Symbol.IsValid(symbol) ? ValidationResult.Success : new ValidationResult(" ");

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
    }

    partial void OnSelectedStrategyChanged(StrategyDescriptor? value)
    {
        StrategyParameters.Clear();
        foreach (var parameter in value?.Parameters ?? [])
            StrategyParameters.Add(new StrategyParameterInput(value!.Name, parameter));
    }

    protected override void OnBusyChanged()
    {
        RunBacktestCommand.NotifyCanExecuteChanged();
        LoadLatestResultsCommand.NotifyCanExecuteChanged();
        CancelCommand.NotifyCanExecuteChanged();
    }

    private bool CanRun() => !IsBusy && !HasErrors;

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task RunBacktestAsync(CancellationToken cancellationToken)
    {
        if (!TryGetInputs(out var strategy, out var parameters, out var start, out var end))
            return;

        var symbol = Symbol.Trim().ToUpperInvariant();
        await RunOperationAsync(Loc.Format("Status_RunningBacktest", strategy.DisplayName, symbol), async operationId =>
        {
            var command = new RunBacktestCommand(strategy.Name, symbol, start, end, InitialCapital, parameters)
            {
                Progress = CreateProgress<BacktestProgress>(operationId, update =>
                {
                    Status.Progress = update.Percent;
                    if (update.Message is { Length: > 0 } message)
                        Status.Show(message);
                })
            };

            var run = await _dispatcher.SendAsync(command, cancellationToken);
            if (run.IsFailure)
                return Describe(run.Error);

            var report = await _dispatcher.SendAsync(new GetBacktestReportQuery(run.Value.Symbol, run.Value.RunId), cancellationToken);
            if (report.IsFailure)
                return Describe(report.Error);

            await ShowReportAsync(report.Value, symbol, start, end, cancellationToken);
            return Loc.Format("Status_BacktestCompleted", run.Value.RunId);
        });

        // Every outcome (completed, failed, cancelled) is stored, so the history changes either way.
        _messenger.Send(new BacktestRunsChangedMessage());
    }

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task RunBacktestBatchAsync(CancellationToken cancellationToken)
    {
        if (!TryGetInputs(out var strategy, out var parameters, out var start, out var end))
            return;

        var symbols = SymbolList.Split(SymbolSeparators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var result = await _dispatcher.SendAsync(
            new QueueBacktestsCommand(strategy.Name, symbols, start, end, InitialCapital, parameters), cancellationToken);

        if (result.IsFailure)
            Status.Show(result.Error);
        else if (result.Value.AlreadyQueued.Count == 0)
            Status.Show(Loc.Format("Status_QueuedBacktests", result.Value.JobIds.Count));
        else
            Status.Show(Loc.Format("Status_QueuedBacktestsSkipped", result.Value.JobIds.Count, string.Join(", ", result.Value.AlreadyQueued)));
    }

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task LoadLatestResultsAsync(CancellationToken cancellationToken)
    {
        if (SelectedStrategy is not { } strategy)
            return;

        var symbol = Symbol.Trim().ToUpperInvariant();
        await RunOperationAsync(Loc.Format("Status_LoadingResults", symbol), async _ =>
        {
            var report = await _dispatcher.SendAsync(new GetLatestBacktestReportQuery(symbol, strategy.Name), cancellationToken);
            if (report.IsFailure)
                return Describe(report.Error);

            await ShowReportAsync(report.Value, symbol, null, null, cancellationToken);
            return Loc.Format("Status_Loaded", report.Value.RunId);
        });
    }

    [RelayCommand(CanExecute = nameof(IsBusy))]
    private void Cancel()
    {
        RunBacktestCommand.Cancel();
        LoadLatestResultsCommand.Cancel();
    }

    /// <summary>From the history page: show a finished run.</summary>
    public async void Receive(OpenBacktestRunMessage message)
    {
        var run = message.Run;
        await RunOperationAsync(Loc.Format("Status_LoadingResults", run.RunId), async _ =>
        {
            var report = await _dispatcher.SendAsync(new GetBacktestReportQuery(run.Symbol, run.RunId));
            if (report.IsFailure)
                return Describe(report.Error);

            await ShowReportAsync(report.Value, run.Symbol, run.Start, run.End, CancellationToken.None);
            return Loc.Format("Status_Loaded", run.RunId);
        });
    }

    /// <summary>From the optimization page: fill the form with a strategy setup, e.g. the best parameters found.</summary>
    /// <summary>Another page (e.g. Strategies) asks to try a strategy: select it with its default parameters.</summary>
    public async void Receive(SelectBacktestStrategyMessage message)
    {
        if (Strategies.Count == 0)
            await LoadAsync();

        SelectedStrategy = Strategies.FirstOrDefault(s => s.Name == message.StrategyName) ?? SelectedStrategy;
    }

    public async void Receive(ApplyBacktestSetupMessage message)
    {
        if (Strategies.Count == 0)
            await LoadAsync();

        SelectedStrategy = Strategies.FirstOrDefault(s => s.Name == message.StrategyName) ?? SelectedStrategy;
        Symbol = message.Symbol;
        StartDate = message.Start.ToDateTime(TimeOnly.MinValue);
        EndDate = message.End.ToDateTime(TimeOnly.MinValue);
        InitialCapital = message.InitialCapital;
        foreach (var input in StrategyParameters)
        {
            if (message.Parameters.TryGetValue(input.Name, out var value))
                input.Value = value;
        }
    }

    private async Task ShowReportAsync(BacktestReport report, string symbol, DateOnly? start, DateOnly? end, CancellationToken cancellationToken)
    {
        string Stat(string name) => report.Statistics.GetValueOrDefault(name, "–");

        ReportTitle = report.RunId;
        CompoundingReturn = Stat("Compounding Annual Return");
        MaxDrawdown = Stat("Drawdown");
        WinRate = Stat("Win Rate");
        SharpeRatio = Stat("Sharpe Ratio");
        AverageWin = Stat("Average Win");
        AverageLoss = Stat("Average Loss");
        ProfitFactor = report.TradeStatistics.ProfitFactor.ToString("0.00", CultureInfo.CurrentCulture);
        TotalTrades = report.TradeStatistics.TotalTrades.ToString(CultureInfo.CurrentCulture);
        MaxConsecutiveWins = report.TradeStatistics.MaxConsecutiveWins;
        MaxConsecutiveLosses = report.TradeStatistics.MaxConsecutiveLosses;
        Trades = report.ClosedTrades;
        Equity = report.Equity.Select(p => new TimeValue(p.Time, (double)p.Value)).ToList();
        TradeMarkers = report.ClosedTrades
            .Select(t => new TradeMarker(t.EntryTime, (double)t.EntryPrice, t.ExitTime, (double)t.ExitPrice, t.Direction == TradeDirection.Long, t.ProfitLoss > 0))
            .ToList();

        // Price chart: daily bars over the report's period (cached; aggregated from minute data when needed).
        var from = start ?? (report.Equity.Count > 0 ? DateOnly.FromDateTime(report.Equity[0].Time) : DateOnly.FromDateTime(DateTime.Today.AddYears(-1)));
        var to = end ?? (report.Equity.Count > 0 ? DateOnly.FromDateTime(report.Equity[^1].Time) : DateOnly.FromDateTime(DateTime.Today));
        var prices = await _dispatcher.SendAsync(new GetPriceBarsQuery(symbol, BarResolution.Daily, from, to), cancellationToken);
        PriceBars = prices.IsSuccess
            ? prices.Value.Bars.Select(b => new PriceBar(b.Time, TimeSpan.FromDays(1), (double)b.Open, (double)b.High, (double)b.Low, (double)b.Close)).ToList()
            : [];
    }

    private bool TryGetInputs(
        [NotNullWhen(true)] out StrategyDescriptor? strategy,
        out IReadOnlyDictionary<string, string> parameters,
        out DateOnly start,
        out DateOnly end)
    {
        strategy = SelectedStrategy;
        parameters = StrategyParameters.ToDictionary(p => p.Name, p => p.Value);
        start = end = default;

        ValidateAllProperties();
        if (HasErrors)
        {
            Status.Show(Loc.Get("Status_FixInputs"));
            return false;
        }

        if (StartDate is null || EndDate is null)
        {
            Status.Show(Loc.Get("Status_SelectDates"));
            return false;
        }

        if (strategy is null)
        {
            Status.Show(Loc.Get("Status_SelectStrategy"));
            return false;
        }

        // Quick feedback from the strategy's own declarations; the command validator checks again.
        var errors = strategy.Validate(parameters);
        if (errors.Count > 0)
        {
            Status.Show(string.Join(" ", errors));
            return false;
        }

        start = DateOnly.FromDateTime(StartDate.Value);
        end = DateOnly.FromDateTime(EndDate.Value);
        return true;
    }

    private static string Describe(Application.Abstractions.Results.Error error) =>
        error.Type == Application.Abstractions.Results.ErrorType.Validation ? error.Message : Loc.Format("Status_Failed", error.Message);
}
