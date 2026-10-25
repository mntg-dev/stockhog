using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using EmTrading.Application.Abstractions.Messaging;
using EmTrading.Application.Backtesting.Strategies;
using EmTrading.Application.Optimization.GetBestStrategyParameters;
using EmTrading.Presentation.Backtesting;
using EmTrading.Presentation.Localization;
using EmTrading.Presentation.Optimization;
using EmTrading.Presentation.Shell;
using Microsoft.Extensions.Logging;

namespace EmTrading.Presentation.Strategies;

/// <summary>
/// What each strategy does and how to use it: the description (resources <c>StrategyDoc_{Name}_{Section}</c>), its
/// parameters with the ranges worth testing, and the best parameters your own optimizations found for it.
/// </summary>
public sealed partial class StrategiesViewModel : PageViewModel, IRecipient<OptimizationsChangedMessage>
{
    /// <summary>Text sections of each strategy's description, in display order.</summary>
    public static readonly IReadOnlyList<string> DocSections = ["Summary", "HowItWorks", "WhenItWorks", "HowToUse"];

    /// <summary>Sections shown after <see cref="DocSections"/> only for strategies that have them, e.g. results of tests.</summary>
    public static readonly IReadOnlyList<string> OptionalDocSections = ["TestResults"];

    private readonly IDispatcher _dispatcher;
    private readonly IMessenger _messenger;

    public StrategiesViewModel(IDispatcher dispatcher, IMessenger messenger, StatusViewModel status, ILogger<StrategiesViewModel> logger)
        : base(status, logger)
    {
        _dispatcher = dispatcher;
        _messenger = messenger;
        messenger.RegisterAll(this);
    }

    [ObservableProperty] private IReadOnlyList<StrategyDescriptor> _strategies = [];
    [ObservableProperty] private StrategyDescriptor? _selectedStrategy;
    [ObservableProperty] private IReadOnlyList<StrategyDocSection> _sections = [];
    [ObservableProperty] private IReadOnlyList<ParameterGuide> _parameters = [];
    [ObservableProperty] private IReadOnlyList<BestParameters> _bestResults = [];

    public bool HasBestResults => BestResults.Count > 0;

    partial void OnBestResultsChanged(IReadOnlyList<BestParameters> value) => OnPropertyChanged(nameof(HasBestResults));

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

    public async void Receive(OptimizationsChangedMessage message) => await LoadBestResultsAsync();

    partial void OnSelectedStrategyChanged(StrategyDescriptor? value)
    {
        Sections = value is null ? [] : DocSectionsOf(value.Name);
        Parameters = value?.Parameters.Select(p => new ParameterGuide(value.Name, p)).ToList() ?? [];
        _ = LoadBestResultsAsync();
    }

    /// <summary>The required sections, then the optional ones this strategy has texts for.</summary>
    public static IReadOnlyList<StrategyDocSection> DocSectionsOf(string strategyName) =>
        DocSections
            .Concat(OptionalDocSections.Where(section => Loc.TryGet(new StrategyDocSection(section, strategyName).Key, out _)))
            .Select(section => new StrategyDocSection(section, strategyName))
            .ToList();

    private async Task LoadBestResultsAsync()
    {
        if (SelectedStrategy is not { } strategy)
        {
            BestResults = [];
            return;
        }

        var best = await _dispatcher.SendAsync(new GetBestStrategyParametersQuery(strategy.Name));
        if (best.IsFailure)
            Status.Show(best.Error);
        else if (SelectedStrategy?.Name == strategy.Name)
            BestResults = best.Value;
    }

    /// <summary>Fills the backtest form with a best result's strategy, symbol, period and parameters.</summary>
    [RelayCommand]
    private void UseInBacktest(BestParameters? result)
    {
        if (SelectedStrategy is not { } strategy || result is null)
            return;

        _messenger.Send(new NavigateMessage(typeof(BacktestViewModel)));
        _messenger.Send(new ApplyBacktestSetupMessage(strategy.Name, result.Symbol, result.Start, result.End, result.InitialCapital, result.Parameters));
    }

    /// <summary>Opens the backtest page with this strategy and its default parameters.</summary>
    [RelayCommand]
    private void TryInBacktest()
    {
        if (SelectedStrategy is not { } strategy)
            return;

        _messenger.Send(new NavigateMessage(typeof(BacktestViewModel)));
        _messenger.Send(new SelectBacktestStrategyMessage(strategy.Name));
    }

    /// <summary>Opens the optimization page with this strategy and its suggested ranges.</summary>
    [RelayCommand]
    private void Optimize()
    {
        if (SelectedStrategy is not { } strategy)
            return;

        _messenger.Send(new NavigateMessage(typeof(OptimizationViewModel)));
        _messenger.Send(new OptimizeStrategyMessage(strategy.Name));
    }
}

/// <summary>One text section of a strategy's description; the view looks up <see cref="Key"/> in the current language.</summary>
public sealed record StrategyDocSection(string Section, string StrategyName)
{
    public string TitleKey => $"Strategies_Section_{Section}";

    public string Key => $"StrategyDoc_{StrategyName}_{Section}";
}

/// <summary>
/// One row of the parameters table. Texts are read on every access: the rows outlive a language change, which only
/// rebuilds the view.
/// </summary>
public sealed record ParameterGuide(string StrategyName, StrategyParameterDescriptor Parameter)
{
    public string DisplayName => StrategyTexts.ParameterName(StrategyName, Parameter);

    public string? Description => StrategyTexts.ParameterDescription(StrategyName, Parameter);

    public string Default => Parameter.FormatValue(Parameter.DefaultValue);

    /// <summary>Valid values, e.g. "2 – 100".</summary>
    public string Allowed => (Parameter.Minimum, Parameter.Maximum) switch
    {
        ({ } min, { } max) => $"{Parameter.FormatValue(min)} – {Parameter.FormatValue(max)}",
        ({ } min, null) => $"≥ {Parameter.FormatValue(min)}",
        (null, { } max) => $"≤ {Parameter.FormatValue(max)}",
        _ => "–"
    };

    /// <summary>Values worth testing in an optimization as "from – to / step", or "–".</summary>
    public string Suggested => Parameter.Suggested is { } range
        ? $"{Parameter.FormatValue(range.From)} – {Parameter.FormatValue(range.To)} / {Parameter.FormatValue(range.Step)}"
        : "–";
}
