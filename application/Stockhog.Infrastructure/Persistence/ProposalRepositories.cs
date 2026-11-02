using Stockhog.Application.Proposals;
using Stockhog.Domain.Proposals;
using Microsoft.EntityFrameworkCore;

namespace Stockhog.Infrastructure.Persistence;

internal sealed class ProposalRepository(TradingDbContext db) : IProposalRepository
{
    public void Add(TradeProposal proposal) => db.TradeProposals.Add(proposal);

    public Task<TradeProposal?> GetAsync(ProposalId id, CancellationToken cancellationToken = default) =>
        db.TradeProposals.FirstOrDefaultAsync(p => p.Id == id, cancellationToken);

    public async Task<IReadOnlyList<TradeProposal>> ListPendingAsync(CancellationToken cancellationToken = default) =>
        await db.TradeProposals.Where(p => p.Status == ProposalStatus.Pending).ToListAsync(cancellationToken).ConfigureAwait(false);
}

internal sealed class ProposalReader(TradingDbContext db) : IProposalReader
{
    public async Task<IReadOnlyList<TradeProposal>> ListAsync(int take, CancellationToken cancellationToken = default) =>
        await db.TradeProposals.AsNoTracking()
            .OrderByDescending(p => p.CreatedAt)
            .Take(take)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
}
