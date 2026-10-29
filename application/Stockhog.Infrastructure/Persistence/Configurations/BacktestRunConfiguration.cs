using Stockhog.Domain.Backtesting;
using Stockhog.Domain.MarketData;
using Stockhog.Domain.Optimization;
using Stockhog.Infrastructure.Persistence.Converters;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Stockhog.Infrastructure.Persistence.Configurations;

/// <summary>
/// Maps the aggregate onto one flat table: single-value objects become converted columns,
/// <see cref="Domain.Common.DateRange"/> and <see cref="Domain.Common.Money"/> become complex properties (column pairs).
/// </summary>
internal sealed class BacktestRunConfiguration : IEntityTypeConfiguration<BacktestRun>
{
    public void Configure(EntityTypeBuilder<BacktestRun> builder)
    {
        builder.ToTable("BacktestRuns");

        builder.HasKey(r => r.Id);
        builder.Property(r => r.Id)
            .HasConversion(id => id.Value, value => BacktestRunId.From(value))
            .HasMaxLength(128)
            .ValueGeneratedNever();

        builder.Property(r => r.Symbol)
            .HasConversion(symbol => symbol.Value, value => Symbol.Create(value))
            .HasMaxLength(Symbol.MaxLength);

        builder.Property(r => r.Strategy)
            .HasColumnName("StrategyName")
            .HasConversion(strategy => strategy.Value, value => StrategyName.Create(value))
            .HasMaxLength(StrategyName.MaxLength);

        builder.ComplexProperty(r => r.Period, period =>
        {
            period.Property(p => p.Start).HasColumnName("PeriodStart");
            period.Property(p => p.End).HasColumnName("PeriodEnd");
        });

        builder.ComplexProperty(r => r.InitialCapital, money =>
        {
            money.Property(m => m.Amount).HasColumnName("InitialCapital");
            money.Property(m => m.Currency).HasColumnName("Currency").HasMaxLength(3);
        });

        builder.Property(r => r.Parameters)
            .HasConversion(new StrategyParametersConverter(), StrategyParametersConverter.Comparer)
            .HasMaxLength(4000);

        // A reference by id: the optimization is another aggregate.
        builder.Property(r => r.OptimizationId)
            .HasColumnName("OptimizationRunId")
            .HasConversion(id => id!.Value, value => OptimizationRunId.From(value))
            .HasMaxLength(128);

        builder.Property(r => r.Status).HasConversion<string>().HasMaxLength(16);
        builder.Property(r => r.Progress);
        builder.Property(r => r.CreatedAt);
        builder.Property(r => r.StartedAt);
        builder.Property(r => r.FinishedAt);
        builder.Property(r => r.FailureReason).HasMaxLength(2000);
        builder.Property(r => r.Metrics)
            .HasConversion(new PerformanceMetricsConverter())
            .HasMaxLength(1000);

        builder.Ignore(r => r.DomainEvents);
        builder.Ignore(r => r.IsFinished);
        builder.Ignore(r => r.Duration);

        builder.HasIndex(r => r.CreatedAt);
        builder.HasIndex(r => new { r.Symbol, r.CreatedAt });
        builder.HasIndex(r => r.Status);
        builder.HasIndex(r => r.OptimizationId);
    }
}
