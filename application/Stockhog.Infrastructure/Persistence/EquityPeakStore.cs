using Stockhog.Application.Trading.Portfolio;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.Extensions.DependencyInjection;

namespace Stockhog.Infrastructure.Persistence;

/// <summary>An account's highest equity. One row per account; paper and live accounts have different numbers.</summary>
internal sealed class EquityPeakRecord
{
    public required string AccountNumber { get; init; }
    public decimal Equity { get; set; }
    public DateTimeOffset At { get; set; }
}

internal sealed class EquityPeakRecordConfiguration : IEntityTypeConfiguration<EquityPeakRecord>
{
    public void Configure(EntityTypeBuilder<EquityPeakRecord> builder)
    {
        builder.ToTable("EquityPeaks");
        builder.HasKey(r => r.AccountNumber);
        builder.Property(r => r.AccountNumber).HasMaxLength(64);
    }
}

/// <summary>
/// Reads and writes in its own scope, like the audit log: the peak is shared by the order checks, the proposals and
/// the periodic guard, and moving it up must not depend on whether the caller's unit of work saves.
/// </summary>
internal sealed class EquityPeakStore(IServiceScopeFactory scopeFactory) : IEquityPeakStore
{
    public async Task<EquityPeak?> GetAsync(string accountNumber, CancellationToken cancellationToken = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TradingDbContext>();
        var record = await db.EquityPeaks.AsNoTracking().FirstOrDefaultAsync(r => r.AccountNumber == accountNumber, cancellationToken).ConfigureAwait(false);
        return record is null ? null : new EquityPeak(record.AccountNumber, record.Equity, record.At);
    }

    public async Task SaveAsync(EquityPeak peak, CancellationToken cancellationToken = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TradingDbContext>();
        var record = await db.EquityPeaks.FirstOrDefaultAsync(r => r.AccountNumber == peak.AccountNumber, cancellationToken).ConfigureAwait(false);
        if (record is null)
        {
            db.EquityPeaks.Add(new EquityPeakRecord { AccountNumber = peak.AccountNumber, Equity = peak.Equity, At = peak.At });
        }
        else if (peak.Equity > record.Equity)
        {
            record.Equity = peak.Equity;
            record.At = peak.At;
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }
}
