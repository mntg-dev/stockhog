using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Stockhog.Application.Abstractions.Messaging;
using Stockhog.Application.Proposals;
using Stockhog.Presentation.Common;
using Stockhog.Presentation.Localization;
using Stockhog.Presentation.Shell;
using Microsoft.Extensions.Logging;

namespace Stockhog.Presentation.Proposals;

/// <summary>
/// Buys the app suggests from fresh strategy signals, each sized by risk with a broker-side stop. Nothing is sent until
/// the user approves a proposal here, after a confirmation that spells out the order.
/// </summary>
public sealed partial class ProposalsViewModel : PageViewModel, IRecipient<ProposalsChangedMessage>
{
    private readonly IDispatcher _dispatcher;
    private readonly IUserConfirmation _confirmation;

    public ProposalsViewModel(IDispatcher dispatcher, IMessenger messenger, IUserConfirmation confirmation, StatusViewModel status, ILogger<ProposalsViewModel> logger)
        : base(status, logger)
    {
        _dispatcher = dispatcher;
        _confirmation = confirmation;
        messenger.RegisterAll(this);
    }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ApproveCommand), nameof(RejectCommand))]
    private ProposalRow? _selectedProposal;

    [ObservableProperty] private IReadOnlyList<ProposalRow> _proposals = [];

    public bool HasProposals => Proposals.Count > 0;

    public int PendingCount => Proposals.Count(p => p.IsPending);

    partial void OnProposalsChanged(IReadOnlyList<ProposalRow> value)
    {
        OnPropertyChanged(nameof(HasProposals));
        OnPropertyChanged(nameof(PendingCount));
    }

    protected override Task LoadAsync() => RefreshAsync(CancellationToken.None);

    public async void Receive(ProposalsChangedMessage message) => await RefreshAsync(CancellationToken.None);

    /// <summary>Expires what is due, then lists the proposals.</summary>
    [RelayCommand]
    private async Task RefreshAsync(CancellationToken cancellationToken)
    {
        await _dispatcher.SendAsync(new ExpireProposalsCommand(), cancellationToken);

        var proposals = await _dispatcher.SendAsync(new GetProposalsQuery(), cancellationToken);
        if (proposals.IsFailure)
        {
            Status.Show(proposals.Error);
            return;
        }

        var selectedId = SelectedProposal?.Id;
        Proposals = proposals.Value;
        SelectedProposal = Proposals.FirstOrDefault(p => p.Id == selectedId) ?? Proposals.FirstOrDefault(p => p.IsPending);
    }

    private bool CanDecide() => SelectedProposal?.IsPending == true && !IsBusy;

    protected override void OnBusyChanged()
    {
        ApproveCommand.NotifyCanExecuteChanged();
        RejectCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanDecide))]
    private async Task ApproveAsync()
    {
        if (SelectedProposal is not { } proposal)
            return;

        var culture = Loc.Instance.Culture;
        var message = string.Format(culture, Loc.Get("Proposals_ConfirmApprove"),
            proposal.Quantity, proposal.Symbol, proposal.ReferencePrice, proposal.StopPrice, proposal.RiskAmount, proposal.RiskPercentOfEquity);
        if (!_confirmation.Confirm(Loc.Get("Proposals_ConfirmApproveTitle"), message))
            return;

        await RunOperationAsync(Loc.Format("Status_ProposalApproving", proposal.Symbol), async _ =>
        {
            var placed = await _dispatcher.SendAsync(new ApproveProposalCommand(proposal.Id));
            await RefreshAsync(CancellationToken.None);
            if (placed.IsFailure)
            {
                Status.Show(placed.Error);
                return Status.Message;
            }

            return Loc.Format("Status_ProposalApproved", proposal.Quantity.ToString("0", CultureInfo.CurrentCulture), proposal.Symbol, placed.Value.ClientOrderId);
        });
    }

    [RelayCommand(CanExecute = nameof(CanDecide))]
    private async Task RejectAsync()
    {
        if (SelectedProposal is not { } proposal)
            return;

        // Cancelling the prompt keeps the proposal; an empty reason rejects it without one.
        if (_confirmation.Prompt(Loc.Get("Proposals_RejectTitle"), Loc.Format("Proposals_RejectPrompt", proposal.Symbol)) is not { } reason)
            return;

        var rejected = await _dispatcher.SendAsync(new RejectProposalCommand(proposal.Id, string.IsNullOrWhiteSpace(reason) ? null : reason));
        if (rejected.IsFailure)
            Status.Show(rejected.Error);
        else
            Status.Show(Loc.Format("Status_ProposalRejected", proposal.Symbol));

        await RefreshAsync(CancellationToken.None);
    }
}
