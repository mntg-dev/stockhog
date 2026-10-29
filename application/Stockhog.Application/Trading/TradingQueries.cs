using System.ComponentModel;
using Stockhog.Application.Abstractions.Events;
using Stockhog.Application.Abstractions.Messaging;
using Stockhog.Application.Abstractions.Results;
using Stockhog.Application.Trading.Safety;
using Stockhog.Domain.Trading;
using FluentValidation;
using Microsoft.Extensions.Caching.Hybrid;

namespace Stockhog.Application.Trading;

/// <summary>
/// The broker account, cached for a few seconds so several views refreshing at once cost one request.
/// Any order change invalidates it.
/// </summary>
public sealed record GetAccountQuery : IQuery<BrokerAccount>, ICacheableQuery
{
    public string CacheKey => "trading:account:summary";

    public IReadOnlyCollection<string> CacheTags => [TradingCacheKeys.AccountTag];

    public TimeSpan CacheDuration => TimeSpan.FromSeconds(5);
}

internal sealed class GetAccountQueryHandler(IBrokerGateway broker) : IQueryHandler<GetAccountQuery, BrokerAccount>
{
    public async Task<Result<BrokerAccount>> HandleAsync(GetAccountQuery query, CancellationToken cancellationToken)
    {
        try
        {
            return await broker.GetAccountAsync(cancellationToken);
        }
        catch (BrokerException ex)
        {
            return TradingErrors.BrokerUnavailable(ex.Message);
        }
    }
}

/// <summary>Open positions, cached like the account and invalidated with it.</summary>
public sealed record GetPositionsQuery : IQuery<PositionList>, ICacheableQuery
{
    public string CacheKey => "trading:account:positions";

    public IReadOnlyCollection<string> CacheTags => [TradingCacheKeys.AccountTag];

    public TimeSpan CacheDuration => TimeSpan.FromSeconds(5);
}

[ImmutableObject(true)]
public sealed record PositionList(IReadOnlyList<BrokerPosition> Positions);

internal sealed class GetPositionsQueryHandler(IBrokerGateway broker) : IQueryHandler<GetPositionsQuery, PositionList>
{
    public async Task<Result<PositionList>> HandleAsync(GetPositionsQuery query, CancellationToken cancellationToken)
    {
        try
        {
            return new PositionList(await broker.GetPositionsAsync(cancellationToken));
        }
        catch (BrokerException ex)
        {
            return TradingErrors.BrokerUnavailable(ex.Message);
        }
    }
}

/// <summary>Orders placed from the app, newest first. Read from storage, which the trade updates keep current.</summary>
public sealed record GetOrdersQuery(bool OpenOnly = false, int Take = 200) : IQuery<IReadOnlyList<OrderSummary>>;

internal sealed class GetOrdersQueryValidator : AbstractValidator<GetOrdersQuery>
{
    public GetOrdersQueryValidator() => RuleFor(q => q.Take).InclusiveBetween(1, 1000);
}

internal sealed class GetOrdersQueryHandler(IOrderReader reader) : IQueryHandler<GetOrdersQuery, IReadOnlyList<OrderSummary>>
{
    public async Task<Result<IReadOnlyList<OrderSummary>>> HandleAsync(GetOrdersQuery query, CancellationToken cancellationToken) =>
        Result<IReadOnlyList<OrderSummary>>.Success(await reader.ListAsync(query.OpenOnly, query.Take, cancellationToken));
}

public sealed record GetRiskLimitsQuery : IQuery<RiskLimits>;

internal sealed class GetRiskLimitsQueryHandler(IRiskLimitsStore store) : IQueryHandler<GetRiskLimitsQuery, RiskLimits>
{
    public Task<Result<RiskLimits>> HandleAsync(GetRiskLimitsQuery query, CancellationToken cancellationToken) =>
        Task.FromResult(Result<RiskLimits>.Success(store.Current));
}

/// <summary>Changes the limits for this session, e.g. flips the kill switch.</summary>
public sealed record UpdateRiskLimitsCommand(RiskLimits Limits) : ICommand<RiskLimits>;

internal sealed class UpdateRiskLimitsCommandValidator : AbstractValidator<UpdateRiskLimitsCommand>
{
    public UpdateRiskLimitsCommandValidator()
    {
        RuleFor(c => c.Limits.MaxOrderNotional).GreaterThan(0).WithMessage("The order limit must be positive.");
        RuleFor(c => c.Limits.MaxPositionNotional).GreaterThan(0).WithMessage("The position limit must be positive.");
        RuleFor(c => c.Limits.MaxDailyLoss).GreaterThan(0).WithMessage("The daily loss limit must be positive.");
        RuleFor(c => c.Limits.MaxOpenOrders).InclusiveBetween(1, 100).WithMessage("Allow between 1 and 100 open orders.");
    }
}

internal sealed class UpdateRiskLimitsCommandHandler(IRiskLimitsStore store, Audit audit) : ICommandHandler<UpdateRiskLimitsCommand, RiskLimits>
{
    public async Task<Result<RiskLimits>> HandleAsync(UpdateRiskLimitsCommand command, CancellationToken cancellationToken)
    {
        var before = store.Current;
        store.Update(command.Limits);

        if (before.TradingEnabled != command.Limits.TradingEnabled)
            await audit.RecordAsync(AuditCategory.Risk, command.Limits.TradingEnabled ? "TradingEnabled" : "TradingDisabled", "Kill switch changed by the user.");
        if (before with { TradingEnabled = command.Limits.TradingEnabled } != command.Limits)
            await audit.RecordAsync(AuditCategory.Risk, "LimitsChanged", $"{before} -> {command.Limits}");

        return store.Current;
    }
}

/// <summary>
/// Submits, fills and closes change cash, buying power and positions: drop the cached account and tell the views.
/// </summary>
internal sealed class InvalidateAccountWhenOrdersChange(HybridCache cache, TradingActivity activity) :
    IDomainEventHandler<OrderSubmitted>,
    IDomainEventHandler<OrderPartiallyFilled>,
    IDomainEventHandler<OrderFilled>,
    IDomainEventHandler<OrderClosed>
{
    public Task HandleAsync(OrderSubmitted domainEvent, CancellationToken cancellationToken) => InvalidateAsync(cancellationToken);

    public Task HandleAsync(OrderPartiallyFilled domainEvent, CancellationToken cancellationToken) => InvalidateAsync(cancellationToken);

    public Task HandleAsync(OrderFilled domainEvent, CancellationToken cancellationToken) => InvalidateAsync(cancellationToken);

    public Task HandleAsync(OrderClosed domainEvent, CancellationToken cancellationToken) => InvalidateAsync(cancellationToken);

    private async Task InvalidateAsync(CancellationToken cancellationToken)
    {
        await cache.RemoveByTagAsync(TradingCacheKeys.AccountTag, cancellationToken);
        activity.Notify();
    }
}
