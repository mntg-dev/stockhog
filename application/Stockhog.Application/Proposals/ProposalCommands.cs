using Stockhog.Application.Abstractions.Messaging;
using Stockhog.Application.Abstractions.Results;
using Stockhog.Application.Common;
using Stockhog.Application.Scanning;
using Stockhog.Application.Trading;
using Stockhog.Application.Trading.Orders;
using Stockhog.Application.Trading.Portfolio;
using Stockhog.Application.Trading.Safety;
using Stockhog.Domain.MarketData;
using Stockhog.Domain.Proposals;
using Stockhog.Domain.Scanning;
using Stockhog.Domain.Signals;
using Stockhog.Domain.Trading;
using FluentValidation;

namespace Stockhog.Application.Proposals;

/// <summary>
/// Turns the fresh entry signals of a scan's passed pairs into buy proposals, sized by <paramref name="Rules"/> against
/// the account and the risk limits. One proposal per symbol (the best-scoring pair); symbols already held or already
/// proposed are skipped. Nothing is sent to the broker: each proposal waits for the user's approval.
/// </summary>
public sealed record CreateProposalsCommand(string ScanId, SizingRules? Rules = null) : ICommand<CreatedProposals>;

/// <param name="Skipped">Pairs with a fresh entry that got no proposal, with the reason.</param>
public sealed record CreatedProposals(IReadOnlyList<string> ProposalIds, IReadOnlyList<SkippedProposal> Skipped);

public sealed record SkippedProposal(string Symbol, string StrategyName, string Reason);

/// <summary>Approves a pending proposal: checks the live price, then places the buy with its stop loss.</summary>
public sealed record ApproveProposalCommand(string ProposalId) : ICommand<PlacedOrder>;

public sealed record RejectProposalCommand(string ProposalId, string? Reason = null) : ICommand<bool>;

/// <summary>Marks pending proposals whose session is over as expired. Sent at startup and when the page opens.</summary>
/// <returns>How many expired now.</returns>
public sealed record ExpireProposalsCommand : ICommand<int>;

internal sealed class CreateProposalsCommandValidator : AbstractValidator<CreateProposalsCommand>
{
    public CreateProposalsCommandValidator() =>
        RuleFor(c => c.ScanId).Must(ScanRunId.IsValid).WithMessage("'{PropertyValue}' is not a valid scan id.");
}

internal sealed class ApproveProposalCommandValidator : AbstractValidator<ApproveProposalCommand>
{
    public ApproveProposalCommandValidator() =>
        RuleFor(c => c.ProposalId).Must(ProposalId.IsValid).WithMessage("'{PropertyValue}' is not a valid proposal id.");
}

internal sealed class RejectProposalCommandValidator : AbstractValidator<RejectProposalCommand>
{
    public RejectProposalCommandValidator()
    {
        RuleFor(c => c.ProposalId).Must(ProposalId.IsValid).WithMessage("'{PropertyValue}' is not a valid proposal id.");
        RuleFor(c => c.Reason).MaximumLength(500);
    }
}

internal sealed class CreateProposalsCommandHandler(
    IScanReader scans,
    IProposalRepository proposals,
    IBrokerGateway broker,
    IRiskLimitsStore riskLimits,
    IOrderRepository orders,
    PortfolioRiskReader portfolio,
    Audit audit,
    TimeProvider clock) : ICommandHandler<CreateProposalsCommand, CreatedProposals>
{
    public async Task<Result<CreatedProposals>> HandleAsync(CreateProposalsCommand command, CancellationToken cancellationToken)
    {
        if (await scans.FindAsync(ScanRunId.From(command.ScanId), cancellationToken) is not { } scan)
            return ScanErrors.NotFound(command.ScanId);

        BrokerAccount account;
        IReadOnlyList<BrokerPosition> positions;
        try
        {
            account = await broker.GetAccountAsync(cancellationToken);
            positions = await broker.GetPositionsAsync(cancellationToken);
        }
        catch (BrokerException ex)
        {
            return TradingErrors.BrokerUnavailable(ex.Message);
        }

        var limits = riskLimits.Current;
        var requested = command.Rules ?? SizingRules.Default;
        var rules = requested with { MaxPositionPercent = Math.Min(requested.MaxPositionPercent, limits.MaxPositionPercent) };
        var now = clock.GetUtcNow();
        var held = positions.Where(p => p.Quantity != 0).Select(p => p.Symbol).ToHashSet(StringComparer.Ordinal);
        var pending = await proposals.ListPendingAsync(cancellationToken);
        var proposed = pending.Select(p => p.Symbol.Value).ToHashSet(StringComparer.Ordinal);
        var risk = await portfolio.ReadAsync(account, positions, cancellationToken);
        var openOrders = (await orders.ListUnfinishedAsync(cancellationToken)).Count;

        var created = new List<string>();
        var skipped = new List<SkippedProposal>();

        // The best-scoring pair per symbol: two strategies agreeing should not double the position.
        var entries = scan.Candidates
            .Where(c => c.Passed && c.Signal?.Kind == SignalKind.Entry)
            .OrderByDescending(c => c.Evaluation!.Score)
            .ToList();

        foreach (var candidate in entries)
        {
            var symbol = candidate.Symbol.Value;
            var signal = candidate.Signal!;
            string? reason = null;

            if (ProposalRules.ExcludedStrategies.Contains(candidate.Strategy.Value))
                reason = "Intraday strategy: its signals are not followed by proposals.";
            else if (held.Contains(symbol))
                reason = "Already held in the account.";
            else if (proposed.Contains(symbol))
                reason = "Already proposed.";

            var expiresAt = ExchangeCalendar.NextSessionClose(signal.AsOf);
            if (reason is null && expiresAt <= now)
                reason = $"The signal of {signal.AsOf:yyyy-MM-dd} was for a session that is over; check the signals again.";

            PositionSize? size = null;
            if (reason is null)
            {
                size = PositionSizer.Size(account.Equity, account.BuyingPower, signal.LastPrice, signal.StopPrice, rules, limits);
                if (size.Quantity < 1)
                    reason = $"Not even one share fits the limits (limited by {size.LimitedBy}).";
            }

            // The checks the order will face on approval, counting the proposals still waiting as if they were bought,
            // so a batch never proposes more than the portfolio limits allow.
            if (reason is null)
            {
                var context = risk.ContextFor(0, account, openOrders) with
                {
                    OpenPositions = risk.OpenPositions + proposed.Count,
                    OrdersToday = risk.OrdersToday + proposed.Count
                };
                var decision = RiskPolicy.Evaluate(new OrderIntent(candidate.Symbol, OrderSide.Buy, size!.Quantity, signal.LastPrice), context, limits);
                if (!decision.IsAllowed)
                    reason = "The order would be refused: " + string.Join(" ", decision.Violations);
            }

            if (reason is not null)
            {
                skipped.Add(new SkippedProposal(symbol, candidate.Strategy.Value, reason));
                continue;
            }

            var proposal = TradeProposal.Create(
                ProposalId.New(candidate.Symbol, now.AddMilliseconds(created.Count)),
                candidate.Symbol,
                candidate.Strategy,
                signal,
                size!,
                account.Equity,
                candidate.Evaluation!.Score,
                scan.Id,
                now,
                expiresAt);
            proposals.Add(proposal);
            proposed.Add(symbol);
            created.Add(proposal.Id.Value);

            await audit.RecordAsync(AuditCategory.Proposal, "Proposed",
                $"Buy {proposal.Quantity} {symbol} near {proposal.ReferencePrice:N2}, stop {proposal.StopPrice:N2}, risk {proposal.RiskAmount:N2} USD " +
                $"({proposal.RiskPercentOfEquity}% of equity), from {candidate.Strategy.Value}" + (size!.UsesFallbackStop ? " (fallback stop)." : "."),
                symbol: symbol);
        }

        // Saved by UnitOfWorkBehavior.
        return new CreatedProposals(created, skipped);
    }
}

/// <summary>
/// The order goes through <see cref="PlaceOrderCommand"/>, so the risk limits, the live-trading arm step and the audit
/// trail apply exactly as for an order from the ticket. A refused order fails the proposal; it is not retried.
/// </summary>
internal sealed class ApproveProposalCommandHandler(
    IProposalRepository proposals,
    IDispatcher dispatcher,
    IQuoteCache quotes,
    ILatestPriceSource latestPrices,
    Audit audit,
    TimeProvider clock) : ICommandHandler<ApproveProposalCommand, PlacedOrder>
{
    public async Task<Result<PlacedOrder>> HandleAsync(ApproveProposalCommand command, CancellationToken cancellationToken)
    {
        if (await proposals.GetAsync(ProposalId.From(command.ProposalId), cancellationToken) is not { } proposal)
            return ProposalErrors.NotFound(command.ProposalId);
        if (!proposal.IsPending)
            return ProposalErrors.NotPending(command.ProposalId, proposal.Status);
        if (proposal.ExpireIfDue(clock.GetUtcNow()))
            return ProposalErrors.Expired(command.ProposalId);

        var livePrice = quotes.TryGet(proposal.Symbol, out var quote) && quote.Price is { } live
            ? live
            : await latestPrices.GetLatestPriceAsync(proposal.Symbol, cancellationToken);
        if (livePrice is not { } price)
            return TradingErrors.NoPrice(proposal.Symbol.Value);

        // Left pending: the price may come back, or the user rejects it.
        if (proposal.ApprovalProblem(price) is { } problem)
            return ProposalErrors.PriceMoved(problem);

        var placed = await dispatcher.SendAsync(
            new PlaceOrderCommand(proposal.Symbol.Value, OrderSide.Buy, OrderType.Market, proposal.Quantity, StopLossPrice: proposal.StopPrice),
            cancellationToken);

        var now = clock.GetUtcNow();
        if (placed.IsSuccess)
        {
            proposal.Submit(ClientOrderId.From(placed.Value.ClientOrderId), now);
            await audit.RecordAsync(AuditCategory.Proposal, "Approved",
                $"Proposal {proposal.Id}: buy {proposal.Quantity} {proposal.Symbol} at about {price:N2}, stop loss {proposal.StopPrice:N2}.",
                placed.Value.ClientOrderId, proposal.Symbol.Value);
            return placed.Value;
        }

        proposal.Fail(placed.Error.Message, null, now);
        await audit.RecordAsync(AuditCategory.Proposal, "Failed", $"Proposal {proposal.Id}: {placed.Error.Message}", symbol: proposal.Symbol.Value);
        return placed.Error;
    }
}

internal sealed class RejectProposalCommandHandler(IProposalRepository proposals, Audit audit, TimeProvider clock)
    : ICommandHandler<RejectProposalCommand, bool>
{
    public async Task<Result<bool>> HandleAsync(RejectProposalCommand command, CancellationToken cancellationToken)
    {
        if (await proposals.GetAsync(ProposalId.From(command.ProposalId), cancellationToken) is not { } proposal)
            return ProposalErrors.NotFound(command.ProposalId);
        if (!proposal.IsPending)
            return ProposalErrors.NotPending(command.ProposalId, proposal.Status);

        proposal.Reject(command.Reason, clock.GetUtcNow());
        await audit.RecordAsync(AuditCategory.Proposal, "Rejected", $"Proposal {proposal.Id}" + (command.Reason is { } r ? $": {r}" : "."),
            symbol: proposal.Symbol.Value);
        return true;
    }
}

internal sealed class ExpireProposalsCommandHandler(IProposalRepository proposals, TimeProvider clock) : ICommandHandler<ExpireProposalsCommand, int>
{
    public async Task<Result<int>> HandleAsync(ExpireProposalsCommand command, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var expired = (await proposals.ListPendingAsync(cancellationToken)).Count(p => p.ExpireIfDue(now));

        // Saved by UnitOfWorkBehavior.
        return expired;
    }
}
