using Stockhog.Application.Instruments;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Stockhog.Infrastructure.Persistence;

/// <summary>A stored instrument. Reference data from the broker, not an aggregate: replaced as a whole on refresh.</summary>
internal sealed class InstrumentRecord
{
    public required string Symbol { get; init; }
    public required string Name { get; init; }
    public required string Exchange { get; init; }
    public DateTimeOffset RefreshedAt { get; init; }
}

internal sealed class InstrumentRecordConfiguration : IEntityTypeConfiguration<InstrumentRecord>
{
    public void Configure(EntityTypeBuilder<InstrumentRecord> builder)
    {
        builder.ToTable("Instruments");
        builder.HasKey(r => r.Symbol);
        builder.Property(r => r.Symbol).HasMaxLength(Domain.MarketData.Symbol.MaxLength);
        builder.Property(r => r.Name).HasMaxLength(256);
        builder.Property(r => r.Exchange).HasMaxLength(16);
    }
}

internal sealed class InstrumentStore(TradingDbContext db) : IInstrumentStore
{
    public async Task<InstrumentCatalogStatus> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        var count = await db.Instruments.CountAsync(cancellationToken).ConfigureAwait(false);
        var refreshedAt = count == 0
            ? null
            : await db.Instruments.MaxAsync(r => (DateTimeOffset?)r.RefreshedAt, cancellationToken).ConfigureAwait(false);

        return new InstrumentCatalogStatus(count, refreshedAt);
    }

    public async Task<IReadOnlyList<InstrumentInfo>> ListAsync(CancellationToken cancellationToken = default) =>
        await db.Instruments.AsNoTracking()
            .OrderBy(r => r.Symbol)
            .Select(r => new InstrumentInfo(r.Symbol, r.Name, r.Exchange))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

    public async Task ReplaceAllAsync(IReadOnlyList<InstrumentInfo> instruments, DateTimeOffset refreshedAt, CancellationToken cancellationToken = default)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        await db.Instruments.ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        db.Instruments.AddRange(instruments.Select(i => new InstrumentRecord
        {
            Symbol = i.Symbol,
            Name = i.Name.Length > 256 ? i.Name[..256] : i.Name,
            Exchange = i.Exchange,
            RefreshedAt = refreshedAt
        }));
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        // Thousands of rows nobody edits: don't keep them tracked for the rest of the scope.
        db.ChangeTracker.Clear();
    }
}
