using System.Text.Json;
using Stockhog.Domain.Common;
using Stockhog.Domain.MarketData;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Stockhog.Infrastructure.Persistence.Configurations;

internal sealed class MarketDataSetConfiguration : IEntityTypeConfiguration<MarketDataSet>
{
    public void Configure(EntityTypeBuilder<MarketDataSet> builder)
    {
        builder.ToTable("MarketDataSets");

        builder.HasKey(s => s.Id);
        builder.Property(s => s.Id)
            .HasConversion(id => id.Value, value => MarketDataSetId.From(value))
            .HasMaxLength(32)
            .ValueGeneratedNever();

        builder.Property(s => s.Symbol)
            .HasConversion(symbol => symbol.Value, value => Symbol.Create(value))
            .HasMaxLength(Symbol.MaxLength);

        builder.Property(s => s.Timeframe).HasConversion<string>().HasMaxLength(16);

        // A handful of ranges per set: one JSON column instead of a child table.
        builder.Property(s => s.Coverage)
            .HasField("_coverage")
            .UsePropertyAccessMode(PropertyAccessMode.Field)
            .HasConversion(new CoverageConverter(), CoverageConverter.Comparer);

        builder.Property(s => s.LastDownloadedAt);

        builder.Ignore(s => s.DomainEvents);
        builder.HasIndex(s => s.Symbol);
    }

    /// <summary>Stores ranges as <c>[{"start":"2026-01-02","end":"2026-03-31"}]</c>.</summary>
    private sealed class CoverageConverter() : ValueConverter<IReadOnlyList<DateRange>, string>(
        ranges => ToJson(ranges),
        json => FromJson(json))
    {
        // The aggregate replaces the list instead of mutating it, so the snapshot can share the instance.
        public static readonly ValueComparer<IReadOnlyList<DateRange>> Comparer = new(
            (left, right) => left!.SequenceEqual(right!),
            ranges => ranges.Aggregate(0, (hash, range) => HashCode.Combine(hash, range)),
            ranges => ranges);

        private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

        private static string ToJson(IReadOnlyList<DateRange> ranges) =>
            JsonSerializer.Serialize(ranges.Select(r => new RangeDto(r.Start, r.End)), JsonOptions);

        private static IReadOnlyList<DateRange> FromJson(string json) =>
            (JsonSerializer.Deserialize<List<RangeDto>>(json, JsonOptions) ?? [])
            .Select(r => DateRange.Create(r.Start, r.End))
            .ToList()
            .AsReadOnly();

        private sealed record RangeDto(DateOnly Start, DateOnly End);
    }
}
