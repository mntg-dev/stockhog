using System.Text.Json;
using System.Text.Json.Serialization;
using Stockhog.Domain.Backtesting;
using Stockhog.Domain.MarketData;
using Stockhog.Domain.Proposals;
using Stockhog.Domain.Scanning;
using Stockhog.Domain.Signals;
using Stockhog.Domain.Trading;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Stockhog.Infrastructure.Persistence.Configurations;

/// <summary>One row per proposal; the signal it follows is a small immutable value object stored as JSON.</summary>
internal sealed class TradeProposalConfiguration : IEntityTypeConfiguration<TradeProposal>
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    public void Configure(EntityTypeBuilder<TradeProposal> builder)
    {
        builder.ToTable("TradeProposals");

        builder.HasKey(p => p.Id);
        builder.Property(p => p.Id)
            .HasConversion(id => id.Value, value => ProposalId.From(value))
            .HasMaxLength(64)
            .ValueGeneratedNever();

        builder.Property(p => p.Symbol)
            .HasConversion(symbol => symbol.Value, value => Symbol.Create(value))
            .HasMaxLength(Symbol.MaxLength);
        builder.Property(p => p.Strategy)
            .HasColumnName("StrategyName")
            .HasConversion(strategy => strategy.Value, value => StrategyName.Create(value))
            .HasMaxLength(StrategyName.MaxLength);
        builder.Property(p => p.Signal)
            .HasConversion(new ValueConverter<StrategySignal, string>(
                signal => JsonSerializer.Serialize(signal, JsonOptions),
                json => JsonSerializer.Deserialize<StrategySignal>(json, JsonOptions)!))
            .HasMaxLength(2000);

        builder.Property(p => p.Quantity);
        builder.Property(p => p.ReferencePrice);
        builder.Property(p => p.StopPrice);
        builder.Property(p => p.RiskAmount);
        builder.Property(p => p.Equity);
        builder.Property(p => p.Score);
        builder.Property(p => p.ScanId)
            .HasConversion(id => id!.Value, value => ScanRunId.From(value))
            .HasMaxLength(64);
        builder.Property(p => p.CreatedAt);
        builder.Property(p => p.ExpiresAt);
        builder.Property(p => p.Status).HasConversion<string>().HasMaxLength(16);
        builder.Property(p => p.DecidedAt);
        builder.Property(p => p.OrderId)
            .HasConversion(id => id!.Value, value => ClientOrderId.From(value))
            .HasMaxLength(ClientOrderId.MaxLength);
        builder.Property(p => p.Reason).HasMaxLength(2000);

        builder.Ignore(p => p.DomainEvents);
        builder.Ignore(p => p.Notional);
        builder.Ignore(p => p.RiskPercentOfEquity);
        builder.Ignore(p => p.IsPending);

        builder.HasIndex(p => p.Status);
        builder.HasIndex(p => p.CreatedAt);
    }
}
