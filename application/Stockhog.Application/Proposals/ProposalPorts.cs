using Stockhog.Application.Abstractions.Results;
using Stockhog.Domain.Proposals;

namespace Stockhog.Application.Proposals;

/// <summary>Write side: loads proposals for changes, which the unit of work saves.</summary>
public interface IProposalRepository
{
    void Add(TradeProposal proposal);

    Task<TradeProposal?> GetAsync(ProposalId id, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<TradeProposal>> ListPendingAsync(CancellationToken cancellationToken = default);
}

/// <summary>Read side: untracked proposals. Changes to them are never saved.</summary>
public interface IProposalReader
{
    /// <returns>Newest first.</returns>
    Task<IReadOnlyList<TradeProposal>> ListAsync(int take, CancellationToken cancellationToken = default);
}

/// <summary>Strategies whose signals are never turned into proposals.</summary>
public static class ProposalRules
{
    /// <summary>
    /// Intraday strategies: they are flat at every close, so a signal read after the close says nothing about the next
    /// session, and following them needs the autopilot, not a once-a-day approval.
    /// </summary>
    public static readonly IReadOnlySet<string> ExcludedStrategies = new HashSet<string>(["OpeningRangeBreakoutStrategy"], StringComparer.Ordinal);
}

public static class ProposalErrors
{
    public static Error NotFound(string id) => Error.NotFound("Proposal.NotFound", $"Proposal {id} does not exist.");

    public static Error NotPending(string id, ProposalStatus status) =>
        new("Proposal.NotPending", $"Proposal {id} is already {status}.", ErrorType.Conflict);

    public static Error Expired(string id) =>
        new("Proposal.Expired", $"Proposal {id} has expired: the session it was meant for is over.", ErrorType.Conflict);

    public static Error PriceMoved(string reason) => new("Proposal.PriceMoved", reason, ErrorType.Conflict);
}
