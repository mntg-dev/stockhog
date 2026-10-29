using Stockhog.Application.Trading.Safety;
using Stockhog.Domain.Trading;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Stockhog.Infrastructure.Persistence;

/// <summary>A stored audit entry; append-only, never updated.</summary>
internal sealed class AuditRecord
{
    public long Id { get; init; }
    public DateTimeOffset Time { get; init; }
    public TradingMode Mode { get; init; }
    public AuditCategory Category { get; init; }
    public required string Action { get; init; }
    public string? ClientOrderId { get; init; }
    public string? Symbol { get; init; }
    public required string Details { get; init; }
}

internal sealed class AuditRecordConfiguration : IEntityTypeConfiguration<AuditRecord>
{
    public void Configure(EntityTypeBuilder<AuditRecord> builder)
    {
        builder.ToTable("AuditLog");
        builder.HasKey(r => r.Id);
        builder.Property(r => r.Id).ValueGeneratedOnAdd();
        builder.Property(r => r.Mode).HasConversion<string>().HasMaxLength(8);
        builder.Property(r => r.Category).HasConversion<string>().HasMaxLength(16);
        builder.Property(r => r.Action).HasMaxLength(64);
        builder.Property(r => r.ClientOrderId).HasMaxLength(ClientOrderId.MaxLength);
        builder.Property(r => r.Symbol).HasMaxLength(Domain.MarketData.Symbol.MaxLength);
        builder.Property(r => r.Details).HasMaxLength(4000);
        builder.HasIndex(r => r.Time);
        builder.HasIndex(r => r.ClientOrderId);
    }
}

/// <summary>
/// Writes each entry in its own scope and transaction, independent of the caller's unit of work, so refused or failed
/// actions (which save nothing else) are recorded too. A failed write is logged, never thrown.
/// </summary>
internal sealed class AuditLog(IServiceScopeFactory scopeFactory, ILogger<AuditLog> logger) : IAuditLog
{
    private const int MaxDetails = 4000;

    public async Task RecordAsync(AuditEntry entry, CancellationToken cancellationToken = default)
    {
        logger.LogInformation("Audit {Mode} {Category} {Action} {ClientOrderId} {Symbol}: {Details}",
            entry.Mode, entry.Category, entry.Action, entry.ClientOrderId, entry.Symbol, entry.Details);
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<TradingDbContext>();
            db.AuditLog.Add(new AuditRecord
            {
                Time = entry.Time,
                Mode = entry.Mode,
                Category = entry.Category,
                Action = entry.Action,
                ClientOrderId = entry.ClientOrderId,
                Symbol = entry.Symbol,
                Details = entry.Details.Length > MaxDetails ? entry.Details[..MaxDetails] : entry.Details
            });
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Could not store audit entry {Action} for {ClientOrderId}", entry.Action, entry.ClientOrderId);
        }
    }

    public async Task<IReadOnlyList<AuditEntry>> ListAsync(int take, CancellationToken cancellationToken = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TradingDbContext>();
        var records = await db.AuditLog.AsNoTracking()
            .OrderByDescending(r => r.Id)
            .Take(take)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return records.Select(r => new AuditEntry(r.Time, r.Mode, r.Category, r.Action, r.ClientOrderId, r.Symbol, r.Details)).ToList();
    }
}
