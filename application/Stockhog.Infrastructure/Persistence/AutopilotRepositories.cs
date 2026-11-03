using Stockhog.Application.Autopilot;
using Stockhog.Domain.Autopilot;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.Extensions.DependencyInjection;

namespace Stockhog.Infrastructure.Persistence;

internal sealed class AutopilotRepository(TradingDbContext db) : IAutopilotRepository
{
    public async Task<Autopilot> GetOrCreateAsync(CancellationToken cancellationToken = default)
    {
        var autopilot = db.Autopilots.Local.FirstOrDefault()
                        ?? await db.Autopilots.FirstOrDefaultAsync(a => a.Id == Autopilot.SingletonId, cancellationToken).ConfigureAwait(false);
        if (autopilot is null)
        {
            autopilot = Autopilot.Create();
            db.Autopilots.Add(autopilot);
        }

        return autopilot;
    }
}

/// <summary>A stored decision; append-only, never updated.</summary>
internal sealed class AutopilotDecisionRecord
{
    public long Id { get; init; }
    public DateTimeOffset Time { get; init; }
    public AutopilotMode Mode { get; init; }
    public int AssignmentId { get; init; }
    public required string Symbol { get; init; }
    public required string StrategyName { get; init; }
    public AutopilotAction Action { get; init; }
    public decimal Quantity { get; init; }
    public decimal? Price { get; init; }
    public decimal? StopPrice { get; init; }
    public required string Reason { get; init; }
    public bool Executed { get; init; }
    public string? OrderId { get; init; }
    public decimal? Profit { get; init; }
}

internal sealed class AutopilotDecisionRecordConfiguration : IEntityTypeConfiguration<AutopilotDecisionRecord>
{
    public void Configure(EntityTypeBuilder<AutopilotDecisionRecord> builder)
    {
        builder.ToTable("AutopilotDecisions");
        builder.HasKey(r => r.Id);
        builder.Property(r => r.Id).ValueGeneratedOnAdd();
        builder.Property(r => r.Mode).HasConversion<string>().HasMaxLength(8);
        builder.Property(r => r.Symbol).HasMaxLength(Domain.MarketData.Symbol.MaxLength);
        builder.Property(r => r.StrategyName).HasMaxLength(Domain.Backtesting.StrategyName.MaxLength);
        builder.Property(r => r.Action).HasConversion<string>().HasMaxLength(8);
        builder.Property(r => r.Reason).HasMaxLength(1000);
        builder.Property(r => r.OrderId).HasMaxLength(128);
        builder.HasIndex(r => r.Time);
    }
}

/// <summary>Writes in its own scope, like the audit log, so a cycle's decisions are kept whatever happens to the cycle.</summary>
internal sealed class AutopilotDecisionLog(IServiceScopeFactory scopeFactory) : IAutopilotDecisionLog
{
    private const int MaxReason = 1000;

    public async Task AppendAsync(IReadOnlyList<AutopilotDecision> decisions, CancellationToken cancellationToken = default)
    {
        if (decisions.Count == 0)
            return;

        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TradingDbContext>();
        db.AutopilotDecisions.AddRange(decisions.Select(d => new AutopilotDecisionRecord
        {
            Time = d.Time,
            Mode = d.Mode,
            AssignmentId = d.AssignmentId,
            Symbol = d.Symbol,
            StrategyName = d.StrategyName,
            Action = d.Action,
            Quantity = d.Quantity,
            Price = d.Price,
            StopPrice = d.StopPrice,
            Reason = d.Reason.Length > MaxReason ? d.Reason[..MaxReason] : d.Reason,
            Executed = d.Executed,
            OrderId = d.OrderId,
            Profit = d.Profit
        }));
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<AutopilotDecision>> ListAsync(int take, CancellationToken cancellationToken = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TradingDbContext>();
        var records = await db.AutopilotDecisions.AsNoTracking()
            .OrderByDescending(r => r.Id)
            .Take(take)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return records
            .Select(r => new AutopilotDecision(r.Time, r.Mode, r.AssignmentId, r.Symbol, r.StrategyName, r.Action, r.Quantity, r.Price, r.StopPrice,
                r.Reason, r.Executed, r.OrderId, r.Profit))
            .ToList();
    }
}
