using System.Text.Json;
using System.Text.Json.Serialization;
using Stockhog.Domain.Backtesting;
using Stockhog.Domain.MarketData;
using Stockhog.Domain.Scanning;
using Stockhog.Domain.Signals;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Stockhog.Infrastructure.Persistence.Configurations;

/// <summary>
/// The scan in one table and its candidates in an owned child table, loaded with it. Thresholds and each candidate's
/// evaluation are small immutable value objects stored as JSON.
/// </summary>
internal sealed class ScanRunConfiguration : IEntityTypeConfiguration<ScanRun>
{
    internal static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    public void Configure(EntityTypeBuilder<ScanRun> builder)
    {
        builder.ToTable("ScanRuns");

        builder.HasKey(r => r.Id);
        builder.Property(r => r.Id)
            .HasConversion(id => id.Value, value => ScanRunId.From(value))
            .HasMaxLength(64)
            .ValueGeneratedNever();

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

        builder.Property(r => r.Thresholds)
            .HasConversion(new JsonValueConverter<ScanThresholds>())
            .HasMaxLength(500);

        builder.Property(r => r.MaxParallel);
        builder.Property(r => r.ScreenerRunId);
        builder.Property(r => r.Status).HasConversion<string>().HasMaxLength(16);
        builder.Property(r => r.CreatedAt);
        builder.Property(r => r.StartedAt);
        builder.Property(r => r.FinishedAt);
        builder.Property(r => r.FailureReason).HasMaxLength(2000);

        builder.OwnsMany(r => r.Candidates, candidate =>
        {
            candidate.ToTable("ScanCandidates");
            candidate.WithOwner().HasForeignKey("ScanRunId");
            candidate.Property<string>("ScanRunId").HasMaxLength(64);
            candidate.HasKey("ScanRunId", nameof(ScanCandidate.Id));

            candidate.Property(c => c.Id).HasColumnName("Index").ValueGeneratedNever();
            candidate.Property(c => c.Symbol)
                .HasConversion(symbol => symbol.Value, value => Symbol.Create(value))
                .HasMaxLength(Symbol.MaxLength);
            candidate.Property(c => c.Strategy)
                .HasColumnName("StrategyName")
                .HasConversion(strategy => strategy.Value, value => StrategyName.Create(value))
                .HasMaxLength(StrategyName.MaxLength);
            candidate.Property(c => c.Status).HasConversion<string>().HasMaxLength(16);
            candidate.Property(c => c.RunId)
                .HasConversion(id => id!.Value, value => BacktestRunId.From(value))
                .HasMaxLength(128);
            candidate.Property(c => c.Evaluation)
                .HasConversion(new NullableJsonValueConverter<CandidateEvaluation>())
                .HasMaxLength(1500);
            candidate.Property(c => c.FailureReason).HasMaxLength(2000);
            candidate.Property(c => c.Signal)
                .HasConversion(new NullableJsonValueConverter<StrategySignal>())
                .HasMaxLength(2000);
            candidate.Property(c => c.SignalCheckedAt);

            candidate.Ignore(c => c.Index);
            candidate.Ignore(c => c.IsFinished);
            candidate.Ignore(c => c.Passed);
        });
        builder.Navigation(r => r.Candidates).HasField("_candidates").UsePropertyAccessMode(PropertyAccessMode.Field);

        builder.Ignore(r => r.DomainEvents);
        builder.Ignore(r => r.Symbols);
        builder.Ignore(r => r.Strategies);
        builder.Ignore(r => r.FinishedCandidates);
        builder.Ignore(r => r.PassedCandidates);
        builder.Ignore(r => r.IsFinished);

        builder.HasIndex(r => r.CreatedAt);
        builder.HasIndex(r => r.Status);
    }

    /// <summary>Immutable records: value equality is the right comparer, so the default one works.</summary>
    private sealed class JsonValueConverter<T>() : ValueConverter<T, string>(
        value => JsonSerializer.Serialize(value, JsonOptions),
        json => JsonSerializer.Deserialize<T>(json, JsonOptions)!)
        where T : class;

    private sealed class NullableJsonValueConverter<T>() : ValueConverter<T?, string?>(
        value => value == null ? null : JsonSerializer.Serialize(value, JsonOptions),
        json => json == null ? null : JsonSerializer.Deserialize<T>(json, JsonOptions))
        where T : class;
}
