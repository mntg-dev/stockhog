using System.Text.Json;
using System.Text.Json.Serialization;
using Stockhog.Application.Screening;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Stockhog.Infrastructure.Persistence;

/// <summary>A finished screener run. A snapshot, not an aggregate: written once, deleted when too old.</summary>
internal sealed class ScreenerRunRecord
{
    public Guid Id { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateOnly AsOf { get; init; }

    /// <summary>The criteria as JSON: they are only ever read back whole.</summary>
    public required string Criteria { get; init; }

    public int UniverseSize { get; init; }
    public int WithData { get; init; }
    public int Matched { get; init; }
    public List<ScreenerRowRecord> Rows { get; init; } = [];
}

internal sealed class ScreenerRowRecord
{
    public Guid RunId { get; init; }
    public int Rank { get; init; }
    public required string Symbol { get; init; }
    public required string Name { get; init; }
    public required string Exchange { get; init; }
    public DateOnly LastDate { get; init; }
    public decimal LastClose { get; init; }
    public decimal AverageDollarVolume { get; init; }
    public decimal AtrPercent { get; init; }
    public decimal? Return1MPercent { get; init; }
    public decimal? Return6MPercent { get; init; }
    public decimal FromHighPercent { get; init; }
    public int TradingDays { get; init; }
}

internal sealed class ScreenerRunRecordConfiguration : IEntityTypeConfiguration<ScreenerRunRecord>
{
    public void Configure(EntityTypeBuilder<ScreenerRunRecord> builder)
    {
        builder.ToTable("ScreenerRuns");
        builder.HasKey(r => r.Id);
        builder.Property(r => r.Criteria).HasMaxLength(2000);
        builder.HasIndex(r => r.CreatedAt);
        builder.HasMany(r => r.Rows).WithOne().HasForeignKey(r => r.RunId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class ScreenerRowRecordConfiguration : IEntityTypeConfiguration<ScreenerRowRecord>
{
    public void Configure(EntityTypeBuilder<ScreenerRowRecord> builder)
    {
        builder.ToTable("ScreenerRows");
        builder.HasKey(r => new { r.RunId, r.Rank });
        builder.Property(r => r.Symbol).HasMaxLength(Domain.MarketData.Symbol.MaxLength);
        builder.Property(r => r.Name).HasMaxLength(256);
        builder.Property(r => r.Exchange).HasMaxLength(16);
    }
}

internal sealed class ScreenerResultStore(TradingDbContext db) : IScreenerResultStore
{
    /// <summary>Older runs are deleted when a new one is saved.</summary>
    public const int KeepRuns = 30;

    private static readonly JsonSerializerOptions Json = new() { Converters = { new JsonStringEnumConverter() } };

    public async Task SaveAsync(ScreenerResult result, CancellationToken cancellationToken = default)
    {
        var summary = result.Summary;
        db.ScreenerRuns.Add(new ScreenerRunRecord
        {
            Id = summary.Id,
            CreatedAt = summary.CreatedAt,
            AsOf = summary.AsOf,
            Criteria = JsonSerializer.Serialize(summary.Criteria, Json),
            UniverseSize = summary.UniverseSize,
            WithData = summary.WithData,
            Matched = summary.Matched,
            Rows = result.Symbols.Select(s => new ScreenerRowRecord
            {
                RunId = summary.Id,
                Rank = s.Rank,
                Symbol = s.Symbol,
                Name = s.Name.Length > 256 ? s.Name[..256] : s.Name,
                Exchange = s.Exchange,
                LastDate = s.Metrics.LastDate,
                LastClose = s.Metrics.LastClose,
                AverageDollarVolume = s.Metrics.AverageDollarVolume,
                AtrPercent = s.Metrics.AtrPercent,
                Return1MPercent = s.Metrics.Return1MPercent,
                Return6MPercent = s.Metrics.Return6MPercent,
                FromHighPercent = s.Metrics.FromHighPercent,
                TradingDays = s.Metrics.TradingDays
            }).ToList()
        });
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        db.ChangeTracker.Clear();

        var stale = await db.ScreenerRuns.OrderByDescending(r => r.CreatedAt).Skip(KeepRuns).Select(r => r.Id).ToListAsync(cancellationToken).ConfigureAwait(false);
        if (stale.Count > 0)
        {
            await db.ScreenerRows.Where(r => stale.Contains(r.RunId)).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
            await db.ScreenerRuns.Where(r => stale.Contains(r.Id)).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task<IReadOnlyList<ScreenerRunSummary>> ListRunsAsync(int take, CancellationToken cancellationToken = default)
    {
        var runs = await db.ScreenerRuns.AsNoTracking()
            .OrderByDescending(r => r.CreatedAt)
            .Take(take)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return runs.Select(ToSummary).ToList();
    }

    public async Task<ScreenerResult?> GetAsync(Guid? id, CancellationToken cancellationToken = default)
    {
        var runs = db.ScreenerRuns.AsNoTracking().Include(r => r.Rows);
        var run = id is { } runId
            ? await runs.FirstOrDefaultAsync(r => r.Id == runId, cancellationToken).ConfigureAwait(false)
            : await runs.OrderByDescending(r => r.CreatedAt).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);

        if (run is null)
            return null;

        var symbols = run.Rows
            .OrderBy(r => r.Rank)
            .Select(r => new ScreenedSymbol(r.Rank, r.Name, r.Exchange, new SymbolMetrics(
                r.Symbol, r.LastDate, r.LastClose, r.AverageDollarVolume, r.AtrPercent, r.Return1MPercent, r.Return6MPercent, r.FromHighPercent, r.TradingDays)))
            .ToList();

        return new ScreenerResult(ToSummary(run), symbols);
    }

    private static ScreenerRunSummary ToSummary(ScreenerRunRecord run) =>
        new(run.Id, run.CreatedAt, run.AsOf, JsonSerializer.Deserialize<ScreenerCriteria>(run.Criteria, Json)!, run.UniverseSize, run.WithData, run.Matched);
}
