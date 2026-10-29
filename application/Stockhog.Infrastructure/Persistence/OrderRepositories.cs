using Stockhog.Application.Trading;
using Stockhog.Domain.Trading;
using Microsoft.EntityFrameworkCore;

namespace Stockhog.Infrastructure.Persistence;

internal sealed class OrderRepository(TradingDbContext db) : IOrderRepository
{
    private static readonly OrderStatus[] Unfinished = [OrderStatus.New, OrderStatus.Submitted, OrderStatus.PartiallyFilled];

    public void Add(Order order) => db.Orders.Add(order);

    public Task<Order?> GetAsync(ClientOrderId id, CancellationToken cancellationToken = default) =>
        db.Orders.FirstOrDefaultAsync(o => o.Id == id, cancellationToken);

    public async Task<IReadOnlyList<Order>> ListUnfinishedAsync(CancellationToken cancellationToken = default) =>
        await db.Orders
            .Where(o => Unfinished.Contains(o.Status))
            .OrderBy(o => o.CreatedAt)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
}

/// <summary>Read side: a no-tracking projection for the orders view.</summary>
internal sealed class OrderReader(TradingDbContext db) : IOrderReader
{
    public async Task<IReadOnlyList<OrderSummary>> ListAsync(bool openOnly, int take, CancellationToken cancellationToken = default)
    {
        var query = db.Orders.AsNoTracking();
        if (openOnly)
            query = query.Where(o => o.Status == OrderStatus.New || o.Status == OrderStatus.Submitted || o.Status == OrderStatus.PartiallyFilled);

        var orders = await query
            .OrderByDescending(o => o.CreatedAt)
            .Take(take)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return orders
            .Select(o => new OrderSummary(
                o.Id.Value, o.BrokerOrderId, o.Symbol.Value, o.Side, o.Type, o.Quantity, o.LimitPrice, o.StopPrice, o.TimeInForce,
                o.Status, o.FilledQuantity, o.AverageFillPrice, o.RejectReason, o.CreatedAt, o.UpdatedAt))
            .ToList();
    }
}
