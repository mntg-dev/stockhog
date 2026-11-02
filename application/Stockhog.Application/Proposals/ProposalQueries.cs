using Stockhog.Application.Abstractions.Messaging;
using Stockhog.Application.Abstractions.Results;
using Stockhog.Domain.Proposals;
using FluentValidation;

namespace Stockhog.Application.Proposals;

/// <summary>One proposal as the proposals page shows it.</summary>
public sealed record ProposalRow(
    string Id,
    string Symbol,
    string StrategyName,
    ProposalStatus Status,
    decimal Quantity,
    decimal ReferencePrice,
    decimal StopPrice,
    decimal Notional,
    decimal RiskAmount,
    decimal RiskPercentOfEquity,
    decimal? Score,
    DateOnly SignalAsOf,
    string? EntryTag,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt,
    DateTimeOffset? DecidedAt,
    string? OrderId,
    string? Reason)
{
    public bool IsPending => Status == ProposalStatus.Pending;

    public static ProposalRow From(TradeProposal p) => new(
        p.Id.Value,
        p.Symbol.Value,
        p.Strategy.Value,
        p.Status,
        p.Quantity,
        p.ReferencePrice,
        p.StopPrice,
        p.Notional,
        p.RiskAmount,
        p.RiskPercentOfEquity,
        p.Score,
        p.Signal.AsOf,
        p.Signal.EntryTag,
        p.CreatedAt,
        p.ExpiresAt,
        p.DecidedAt,
        p.OrderId?.Value,
        p.Reason);
}

/// <summary>Pending proposals first (soonest to expire first), then the decided ones, newest first.</summary>
public sealed record GetProposalsQuery(int Take = 200) : IQuery<IReadOnlyList<ProposalRow>>;

internal sealed class GetProposalsQueryValidator : AbstractValidator<GetProposalsQuery>
{
    public GetProposalsQueryValidator() => RuleFor(q => q.Take).InclusiveBetween(1, 1000);
}

internal sealed class GetProposalsQueryHandler(IProposalReader reader) : IQueryHandler<GetProposalsQuery, IReadOnlyList<ProposalRow>>
{
    public async Task<Result<IReadOnlyList<ProposalRow>>> HandleAsync(GetProposalsQuery query, CancellationToken cancellationToken)
    {
        var proposals = await reader.ListAsync(query.Take, cancellationToken);
        var rows = proposals
            .Select(ProposalRow.From)
            .OrderByDescending(p => p.IsPending)
            .ThenBy(p => p.IsPending ? p.ExpiresAt : DateTimeOffset.MaxValue)
            .ThenByDescending(p => p.CreatedAt)
            .ToList();
        return rows;
    }
}
