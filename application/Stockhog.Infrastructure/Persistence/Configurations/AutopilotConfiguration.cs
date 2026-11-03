using Stockhog.Domain.Autopilot;
using Stockhog.Domain.Backtesting;
using Stockhog.Domain.MarketData;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Stockhog.Infrastructure.Persistence.Configurations;

/// <summary>The one autopilot row, with the pairs it follows in an owned child table loaded with it.</summary>
internal sealed class AutopilotConfiguration : IEntityTypeConfiguration<Autopilot>
{
    public void Configure(EntityTypeBuilder<Autopilot> builder)
    {
        builder.ToTable("Autopilot");

        builder.HasKey(a => a.Id);
        builder.Property(a => a.Id).HasMaxLength(32).ValueGeneratedNever();
        builder.Property(a => a.Mode).HasConversion<string>().HasMaxLength(8);
        builder.Property(a => a.MaxConsecutiveLosses);
        builder.Property(a => a.MaxCapitalPercent);
        builder.Property(a => a.LastCycleSession);
        builder.Property(a => a.LastCycleAt);
        builder.Property(a => a.LastAssignmentId);

        builder.OwnsMany(a => a.Assignments, assignment =>
        {
            assignment.ToTable("AutopilotAssignments");
            assignment.WithOwner().HasForeignKey("AutopilotId");
            assignment.Property<string>("AutopilotId").HasMaxLength(32);
            assignment.HasKey("AutopilotId", nameof(AutopilotAssignment.Id));

            assignment.Property(x => x.Id).ValueGeneratedNever();
            assignment.Property(x => x.Symbol)
                .HasConversion(symbol => symbol.Value, value => Symbol.Create(value))
                .HasMaxLength(Symbol.MaxLength);
            assignment.Property(x => x.Strategy)
                .HasColumnName("StrategyName")
                .HasConversion(strategy => strategy.Value, value => StrategyName.Create(value))
                .HasMaxLength(StrategyName.MaxLength);
            assignment.Property(x => x.Score);
            assignment.Property(x => x.AddedAt);
            assignment.Property(x => x.Enabled);
            assignment.Property(x => x.PausedReason).HasMaxLength(500);
            assignment.Property(x => x.Quantity);
            assignment.Property(x => x.EntryPrice);
            assignment.Property(x => x.EntryAt);
            assignment.Property(x => x.StopPrice);
            assignment.Property(x => x.PositionMode).HasConversion<string>().HasMaxLength(8);
            assignment.Property(x => x.EntryOrderId).HasMaxLength(Domain.Trading.ClientOrderId.MaxLength);
            assignment.Property(x => x.RealizedProfit);
            assignment.Property(x => x.ClosedTrades);
            assignment.Property(x => x.WinningTrades);
            assignment.Property(x => x.ConsecutiveLosses);

            assignment.Ignore(x => x.IsActive);
            assignment.Ignore(x => x.IsHolding);
            assignment.Ignore(x => x.IsEntryPending);
        });
        builder.Navigation(a => a.Assignments).HasField("_assignments").UsePropertyAccessMode(PropertyAccessMode.Field);

        builder.Ignore(a => a.DomainEvents);
        builder.Ignore(a => a.CapitalInUse);
        builder.Ignore(a => a.PaperPositions);
    }
}
