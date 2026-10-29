using System.Text.Json;
using Stockhog.Domain.Backtesting;
using Stockhog.Domain.MarketData;
using Stockhog.Domain.Optimization;
using Stockhog.Infrastructure.Persistence.Converters;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Stockhog.Infrastructure.Persistence.Configurations;

/// <summary>
/// The optimization in one table and its trials in an owned child table (they are part of the aggregate and
/// loaded with it). The grid and each trial's metrics are small value objects stored as JSON.
/// </summary>
internal sealed class OptimizationRunConfiguration : IEntityTypeConfiguration<OptimizationRun>
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public void Configure(EntityTypeBuilder<OptimizationRun> builder)
    {
        builder.ToTable("OptimizationRuns");

        builder.HasKey(r => r.Id);
        builder.Property(r => r.Id)
            .HasConversion(id => id.Value, value => OptimizationRunId.From(value))
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

        builder.Property(r => r.Grid)
            .HasConversion(new GridConverter(), GridConverter.Comparer)
            .HasMaxLength(2000);

        builder.Property(r => r.Objective).HasConversion<string>().HasMaxLength(32);
        builder.Property(r => r.MaxParallelTrials);
        builder.Property(r => r.Status).HasConversion<string>().HasMaxLength(16);
        builder.Property(r => r.CreatedAt);
        builder.Property(r => r.StartedAt);
        builder.Property(r => r.FinishedAt);
        builder.Property(r => r.FailureReason).HasMaxLength(2000);
        builder.Property(r => r.BestTrialIndex);

        // DateRange cannot be empty, so the optional check period is stored as two nullable columns.
        builder.Property<DateOnly?>("_checkStart").HasColumnName("CheckStart");
        builder.Property<DateOnly?>("_checkEnd").HasColumnName("CheckEnd");
        builder.Ignore(r => r.CheckPeriod);

        builder.OwnsMany(r => r.Trials, trial =>
        {
            trial.ToTable("OptimizationTrials");
            trial.WithOwner().HasForeignKey("OptimizationRunId");
            trial.Property<string>("OptimizationRunId").HasMaxLength(128);
            trial.HasKey("OptimizationRunId", nameof(OptimizationTrial.Id));

            trial.Property(t => t.Id).HasColumnName("Index").ValueGeneratedNever();
            trial.Property(t => t.Parameters)
                .HasConversion(new StrategyParametersConverter(), StrategyParametersConverter.Comparer)
                .HasMaxLength(4000);
            trial.Property(t => t.Status).HasConversion<string>().HasMaxLength(16);
            trial.Property(t => t.RunId)
                .HasConversion(id => id!.Value, value => BacktestRunId.From(value))
                .HasMaxLength(128);
            trial.Property(t => t.Metrics)
                .HasConversion(new PerformanceMetricsConverter())
                .HasMaxLength(1000);
            trial.Property(t => t.FailureReason).HasMaxLength(2000);

            trial.Property(t => t.CheckStatus).HasConversion<string>().HasMaxLength(16);
            trial.Property(t => t.CheckRunId)
                .HasConversion(id => id!.Value, value => BacktestRunId.From(value))
                .HasMaxLength(128);
            trial.Property(t => t.CheckMetrics)
                .HasConversion(new PerformanceMetricsConverter())
                .HasMaxLength(1000);
            trial.Property(t => t.CheckFailureReason).HasMaxLength(2000);

            trial.Ignore(t => t.Index);
            trial.Ignore(t => t.IsFinished);
        });
        builder.Navigation(r => r.Trials).HasField("_trials").UsePropertyAccessMode(PropertyAccessMode.Field);

        builder.Ignore(r => r.DomainEvents);
        builder.Ignore(r => r.BestTrial);
        builder.Ignore(r => r.FinishedTrials);
        builder.Ignore(r => r.IsFinished);
        builder.Ignore(r => r.Duration);

        builder.HasIndex(r => r.CreatedAt);
        builder.HasIndex(r => r.Status);
    }

    /// <summary>Stores the ranges as <c>[{"name":"fast-period","from":5,"to":50,"step":5}]</c>.</summary>
    private sealed class GridConverter() : ValueConverter<ParameterGrid, string>(
        grid => ToJson(grid),
        json => FromJson(json))
    {
        // Immutable value object: the snapshot can share the instance.
        public static readonly ValueComparer<ParameterGrid> Comparer = new(
            (left, right) => Equals(left, right),
            grid => grid.GetHashCode(),
            grid => grid);

        private static string ToJson(ParameterGrid grid) =>
            JsonSerializer.Serialize(grid.Ranges.Select(r => new RangeDto(r.Name, r.From, r.To, r.Step)), JsonOptions);

        private static ParameterGrid FromJson(string json) =>
            ParameterGrid.Create((JsonSerializer.Deserialize<List<RangeDto>>(json, JsonOptions) ?? [])
                .Select(r => ParameterRange.Create(r.Name, r.From, r.To, r.Step)));

        private sealed record RangeDto(string Name, decimal From, decimal To, decimal Step);
    }
}
