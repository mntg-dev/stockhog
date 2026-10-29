using Stockhog.Domain.MarketData;
using Stockhog.Domain.Trading;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Stockhog.Infrastructure.Persistence.Configurations;

internal sealed class OrderConfiguration : IEntityTypeConfiguration<Order>
{
    public void Configure(EntityTypeBuilder<Order> builder)
    {
        builder.ToTable("Orders");

        builder.HasKey(o => o.Id);
        builder.Property(o => o.Id)
            .HasColumnName("ClientOrderId")
            .HasConversion(id => id.Value, value => ClientOrderId.From(value))
            .HasMaxLength(ClientOrderId.MaxLength)
            .ValueGeneratedNever();

        builder.Property(o => o.Symbol)
            .HasConversion(symbol => symbol.Value, value => Symbol.Create(value))
            .HasMaxLength(Symbol.MaxLength);

        builder.Property(o => o.Side).HasConversion<string>().HasMaxLength(8);
        builder.Property(o => o.Type).HasConversion<string>().HasMaxLength(16);
        builder.Property(o => o.TimeInForce).HasConversion<string>().HasMaxLength(8);
        builder.Property(o => o.Status).HasConversion<string>().HasMaxLength(16);
        builder.Property(o => o.Quantity);
        builder.Property(o => o.LimitPrice);
        builder.Property(o => o.StopPrice);
        builder.Property(o => o.BrokerOrderId).HasMaxLength(64);
        builder.Property(o => o.FilledQuantity);
        builder.Property(o => o.AverageFillPrice);
        builder.Property(o => o.RejectReason).HasMaxLength(2000);
        builder.Property(o => o.CreatedAt);
        builder.Property(o => o.SubmittedAt);
        builder.Property(o => o.UpdatedAt);
        builder.Property(o => o.Version).IsConcurrencyToken();

        builder.Ignore(o => o.DomainEvents);
        builder.Ignore(o => o.IsFinal);
        builder.Ignore(o => o.IsOpen);
        builder.Ignore(o => o.RemainingQuantity);

        builder.HasIndex(o => o.CreatedAt);
        builder.HasIndex(o => o.Status);
        builder.HasIndex(o => o.BrokerOrderId);
    }
}
