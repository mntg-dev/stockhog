using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Stockhog.Application.Abstractions.Messaging;
using Stockhog.Application.Autopilot;
using Stockhog.Application.Backtesting.GetBacktestHistory;
using Stockhog.Application.MarketData.GetMarketDataCoverage;
using Stockhog.Application.Proposals;
using Stockhog.Application.Scanning;
using Stockhog.Application.Trading.Portfolio;
using Stockhog.Domain.Autopilot;
using Stockhog.Presentation.Autopilot;
using Stockhog.Presentation.Backtesting;
using Stockhog.Presentation.Localization;
using Stockhog.Presentation.MarketData;
using Stockhog.Presentation.Proposals;
using Stockhog.Presentation.Scanning;
using Stockhog.Presentation.Settings;
using Stockhog.Presentation.Shell;
using Microsoft.Extensions.Logging;

namespace Stockhog.Presentation.Home;

/// <summary>One step from a fresh install to the autopilot, ticked off once it has been done.</summary>
/// <param name="Page">Where the step is done.</param>
public sealed record GettingStartedStep(string TitleKey, string HintKey, bool IsDone, Type Page)
{
    public string Title => Loc.Get(TitleKey);

    public string Hint => Loc.Get(HintKey);
}

/// <summary>
/// The first page: what to do next on a fresh install, and on a set-up one how the account, the autopilot and the
/// proposals stand, each with a way to the page that handles it.
/// </summary>
public sealed partial class HomeViewModel : PageViewModel,
    IRecipient<BacktestRunsChangedMessage>,
    IRecipient<MarketDataChangedMessage>,
    IRecipient<ScansChangedMessage>,
    IRecipient<AutopilotChangedMessage>,
    IRecipient<ProposalsChangedMessage>
{
    private readonly IDispatcher _dispatcher;
    private readonly IMessenger _messenger;
    private readonly AppEnvironmentInfo _environment;

    public HomeViewModel(IDispatcher dispatcher, IMessenger messenger, AppEnvironmentInfo environment, StatusViewModel status, ILogger<HomeViewModel> logger)
        : base(status, logger)
    {
        _dispatcher = dispatcher;
        _messenger = messenger;
        _environment = environment;
        messenger.RegisterAll(this);
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSetUp), nameof(StepsDone))]
    private IReadOnlyList<GettingStartedStep> _steps = [];

    /// <summary>Every getting-started step is done; the checklist makes room for the overview.</summary>
    public bool IsSetUp => Steps.Count > 0 && Steps.All(s => s.IsDone);

    public string StepsDone => Loc.Format("Home_StepsDone", Steps.Count(s => s.IsDone), Steps.Count);

    [ObservableProperty] private string _accountText = "";

    [ObservableProperty] private bool _accountAtLimit;

    [ObservableProperty] private string _autopilotText = "";

    [ObservableProperty] private string _proposalsText = "";

    [ObservableProperty] private int _pendingProposals;

    protected override Task LoadAsync() => RefreshAsync();

    public async void Receive(BacktestRunsChangedMessage message) => await RefreshAsync();

    public async void Receive(MarketDataChangedMessage message) => await RefreshAsync();

    public async void Receive(ScansChangedMessage message) => await RefreshAsync();

    public async void Receive(AutopilotChangedMessage message) => await RefreshAsync();

    public async void Receive(ProposalsChangedMessage message) => await RefreshAsync();

    [RelayCommand]
    private async Task RefreshAsync()
    {
        var coverage = await _dispatcher.SendAsync(new GetMarketDataCoverageQuery());
        var runs = await _dispatcher.SendAsync(new GetBacktestHistoryQuery(Take: 1));
        var scans = await _dispatcher.SendAsync(new GetScansQuery(Take: 1));
        var autopilot = await _dispatcher.SendAsync(new GetAutopilotQuery());
        var proposals = await _dispatcher.SendAsync(new GetProposalsQuery(Take: 50));

        var following = autopilot.IsSuccess && autopilot.Value.Assignments.Count > 0;
        Steps =
        [
            new("Home_StepKeys", "Home_StepKeysHint", _environment.AlpacaCredentialsConfigured, typeof(SettingsViewModel)),
            new("Home_StepData", "Home_StepDataHint", coverage is { IsSuccess: true, Value.Count: > 0 }, typeof(MarketDataViewModel)),
            new("Home_StepBacktest", "Home_StepBacktestHint", runs is { IsSuccess: true, Value.Count: > 0 }, typeof(BacktestViewModel)),
            new("Home_StepScan", "Home_StepScanHint", scans is { IsSuccess: true, Value.Count: > 0 }, typeof(ScannerViewModel)),
            new("Home_StepAutopilot", "Home_StepAutopilotHint", following && autopilot.Value.Mode != AutopilotMode.Off, typeof(AutopilotViewModel))
        ];

        AutopilotText = autopilot.IsSuccess
            ? Loc.Format("Home_Autopilot", ModeText(autopilot.Value.Mode), autopilot.Value.Assignments.Count,
                autopilot.Value.Assignments.Count(a => a.IsHolding),
                autopilot.Value.LastCycleAt is { } at ? at.LocalDateTime.ToString("g", Loc.Instance.Culture) : Loc.Get("Autopilot_Never"))
            : "";

        PendingProposals = proposals.IsSuccess ? proposals.Value.Count(p => p.IsPending) : 0;
        ProposalsText = PendingProposals > 0 ? Loc.Format("Home_ProposalsPending", PendingProposals) : Loc.Get("Home_ProposalsNone");

        if (!_environment.AlpacaCredentialsConfigured)
        {
            AccountText = Loc.Get("Home_AccountNoKeys");
            AccountAtLimit = false;
            return;
        }

        var risk = await _dispatcher.SendAsync(new GetPortfolioRiskQuery());
        if (risk.IsFailure)
        {
            AccountText = Loc.Format("Home_AccountUnavailable", risk.Error.Message);
            AccountAtLimit = false;
            return;
        }

        var p = risk.Value;
        AccountText = Loc.Format("Home_AccountSummary", p.Equity, p.DrawdownPercent, p.Limits.MaxDrawdownPercent, p.OpenPositions, p.Limits.MaxOpenPositions);
        AccountAtLimit = p.DrawdownLimitReached || p.PositionLimitReached || p.OrderLimitReached;
    }

    /// <summary>Keys spelled out, so the localization check finds every one of them.</summary>
    private static string ModeText(AutopilotMode mode) => Loc.Get(mode switch
    {
        AutopilotMode.Shadow => "AutopilotMode_Shadow",
        AutopilotMode.Paper => "AutopilotMode_Paper",
        AutopilotMode.Live => "AutopilotMode_Live",
        _ => "AutopilotMode_Off"
    });

    [RelayCommand]
    private void Open(Type page) => _messenger.Send(new NavigateMessage(page));

    [RelayCommand]
    private void OpenStep(GettingStartedStep step) => _messenger.Send(new NavigateMessage(step.Page));
}
