using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Stockhog.Application.Abstractions.Messaging;
using Stockhog.Application.Autopilot;
using Stockhog.Domain.Autopilot;
using Stockhog.Presentation.Common;
using Stockhog.Presentation.Localization;
using Stockhog.Presentation.Shell;
using Stockhog.Presentation.Strategies;
using Microsoft.Extensions.Logging;

namespace Stockhog.Presentation.Autopilot;

/// <summary>
/// The automatic trader: the pairs it follows, its mode and limits, and the log of what it decided. In shadow mode it
/// records its decisions and simulates the positions; in paper mode, switched on only after a confirmation, it places
/// the orders on the paper account. Live cannot be chosen yet.
/// </summary>
public sealed partial class AutopilotViewModel : PageViewModel, IRecipient<AutopilotChangedMessage>
{
    private readonly IDispatcher _dispatcher;
    private readonly IUserConfirmation _confirmation;
    private AutopilotMode _mode;

    public AutopilotViewModel(IDispatcher dispatcher, IMessenger messenger, IUserConfirmation confirmation, StatusViewModel status, ILogger<AutopilotViewModel> logger)
        : base(status, logger)
    {
        _dispatcher = dispatcher;
        _confirmation = confirmation;
        messenger.RegisterAll(this);
    }

    [ObservableProperty] private AutopilotView? _autopilot;

    [ObservableProperty] private IReadOnlyList<AutopilotAssignmentView> _assignments = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ToggleEnabledCommand), nameof(ResumeCommand), nameof(RemoveCommand))]
    private AutopilotAssignmentView? _selectedAssignment;

    [ObservableProperty] private IReadOnlyList<AutopilotDecision> _decisions = [];

    [ObservableProperty] private int _maxConsecutiveLosses = 3;

    [ObservableProperty] private decimal _maxCapitalPercent = 50m;

    // One property per radio button. Checking one sends the change at once; unchecking is the other button's doing.
    public bool IsOff
    {
        get => _mode == AutopilotMode.Off;
        set => Choose(value, AutopilotMode.Off);
    }

    public bool IsShadow
    {
        get => _mode == AutopilotMode.Shadow;
        set => Choose(value, AutopilotMode.Shadow);
    }

    public bool IsPaper
    {
        get => _mode == AutopilotMode.Paper;
        set => Choose(value, AutopilotMode.Paper);
    }

    private void Choose(bool chosen, AutopilotMode mode)
    {
        if (chosen && _mode != mode)
            _ = SetModeAsync(mode);
    }

    public bool HasAssignments => Assignments.Count > 0;

    public string SummaryText => Autopilot is not { } a
        ? ""
        : Loc.Format("Autopilot_Summary",
            a.LastCycleAt is { } at ? at.LocalDateTime.ToString("g", Loc.Instance.Culture) : Loc.Get("Autopilot_Never"),
            a.Assignments.Count(x => x.IsHolding),
            a.CapitalInUse,
            a.Assignments.Sum(x => x.RealizedProfit));

    partial void OnAutopilotChanged(AutopilotView? value) => OnPropertyChanged(nameof(SummaryText));

    partial void OnAssignmentsChanged(IReadOnlyList<AutopilotAssignmentView> value) => OnPropertyChanged(nameof(HasAssignments));

    protected override Task LoadAsync() => RefreshAsync(CancellationToken.None);

    public async void Receive(AutopilotChangedMessage message) => await RefreshAsync(CancellationToken.None);

    [RelayCommand]
    private async Task RefreshAsync(CancellationToken cancellationToken)
    {
        var autopilot = await _dispatcher.SendAsync(new GetAutopilotQuery(), cancellationToken);
        if (autopilot.IsFailure)
        {
            Status.Show(autopilot.Error);
            return;
        }

        var decisions = await _dispatcher.SendAsync(new GetAutopilotDecisionsQuery(), cancellationToken);
        if (decisions.IsSuccess)
            Decisions = decisions.Value;

        var selectedId = SelectedAssignment?.Id;
        Autopilot = autopilot.Value;
        Assignments = autopilot.Value.Assignments;
        SelectedAssignment = Assignments.FirstOrDefault(a => a.Id == selectedId);
        MaxConsecutiveLosses = autopilot.Value.MaxConsecutiveLosses;
        MaxCapitalPercent = autopilot.Value.MaxCapitalPercent;
        ShowMode(autopilot.Value.Mode);
    }

    /// <summary>Shows the stored mode without sending it back.</summary>
    private void ShowMode(AutopilotMode mode)
    {
        _mode = mode;
        OnPropertyChanged(nameof(IsOff));
        OnPropertyChanged(nameof(IsShadow));
        OnPropertyChanged(nameof(IsPaper));
    }

    /// <summary>Paper mode places orders by itself, so it is switched on only after the user confirms what it will do.</summary>
    private async Task SetModeAsync(AutopilotMode mode)
    {
        if (mode == AutopilotMode.Paper)
        {
            var message = string.Format(Loc.Instance.Culture, Loc.Get("Autopilot_ConfirmPaper"),
                Assignments.Count(a => a.Enabled && !a.IsPaused), MaxCapitalPercent);
            if (!_confirmation.Confirm(Loc.Get("Autopilot_ConfirmPaperTitle"), message))
            {
                ShowMode(_mode);
                return;
            }
        }

        var set = await _dispatcher.SendAsync(new SetAutopilotModeCommand(mode));
        if (set.IsFailure)
        {
            Status.Show(set.Error);
            await RefreshAsync(CancellationToken.None);
            return;
        }

        Status.Show(Loc.Format("Status_AutopilotMode", Loc.Get(set.Value switch
        {
            AutopilotMode.Shadow => "AutopilotMode_Shadow",
            AutopilotMode.Paper => "AutopilotMode_Paper",
            _ => "AutopilotMode_Off"
        })));
        await RefreshAsync(CancellationToken.None);
    }

    /// <summary>Decides now instead of waiting for the session's scheduled cycle; runs as a background job.</summary>
    [RelayCommand]
    private async Task RunCycleAsync(CancellationToken cancellationToken)
    {
        var queued = await _dispatcher.SendAsync(new QueueAutopilotCycleCommand(), cancellationToken);
        if (queued.IsFailure)
            Status.Show(queued.Error);
        else
            Status.Show(queued.Value.WasQueued ? Loc.Format("Status_AutopilotCycleQueued", queued.Value.Pairs) : Loc.Get("Status_AutopilotCycleRunning"));
    }

    [RelayCommand]
    private async Task SaveLimitsAsync(CancellationToken cancellationToken)
    {
        var saved = await _dispatcher.SendAsync(new ConfigureAutopilotCommand(MaxConsecutiveLosses, MaxCapitalPercent), cancellationToken);
        Status.Show(saved.IsFailure ? saved.Error.Message : Loc.Get("Status_AutopilotLimitsSaved"));
        await RefreshAsync(cancellationToken);
    }

    private bool HasSelection() => SelectedAssignment is not null;

    private bool IsSelectionPaused() => SelectedAssignment?.IsPaused == true;

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task ToggleEnabledAsync()
    {
        if (SelectedAssignment is not { } pair)
            return;

        await ShowResultAsync(await _dispatcher.SendAsync(new SetAutopilotAssignmentEnabledCommand(pair.Id, !pair.Enabled)));
    }

    [RelayCommand(CanExecute = nameof(IsSelectionPaused))]
    private async Task ResumeAsync()
    {
        if (SelectedAssignment is not { } pair)
            return;

        await ShowResultAsync(await _dispatcher.SendAsync(new ResumeAutopilotAssignmentCommand(pair.Id)));
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task RemoveAsync()
    {
        if (SelectedAssignment is not { } pair)
            return;

        var message = string.Format(Loc.Instance.Culture, Loc.Get("Autopilot_ConfirmRemove"), pair.Symbol, StrategyTexts.Name(pair.StrategyName));
        if (!_confirmation.Confirm(Loc.Get("Autopilot_ConfirmRemoveTitle"), message))
            return;

        await ShowResultAsync(await _dispatcher.SendAsync(new RemoveAutopilotAssignmentCommand(pair.Id)));
    }

    private async Task ShowResultAsync(Application.Abstractions.Results.Result<bool> result)
    {
        if (result.IsFailure)
            Status.Show(result.Error);
        await RefreshAsync(CancellationToken.None);
    }
}
