using Stockhog.Application.Abstractions.Messaging;
using Stockhog.Application.Abstractions.Results;
using Stockhog.Application.Common;
using Stockhog.Application.Notifications;
using Stockhog.Application.Trading.Orders;
using Stockhog.Application.Trading.Safety;
using Stockhog.Domain.Trading;

namespace Stockhog.Application.Trading.Portfolio;

/// <summary>The highest equity an account has reached: the reference for the drawdown limit.</summary>
public sealed record EquityPeak(string AccountNumber, decimal Equity, DateTimeOffset At);

/// <summary>Stores each account's high-water mark, so a drawdown is measured across sessions, not just since startup.</summary>
public interface IEquityPeakStore
{
    Task<EquityPeak?> GetAsync(string accountNumber, CancellationToken cancellationToken = default);

    Task SaveAsync(EquityPeak peak, CancellationToken cancellationToken = default);
}

/// <summary>The account measured against the portfolio limits.</summary>
/// <param name="DrawdownPercent">How far equity is below its peak, e.g. 4.2 for 4.2%.</param>
/// <param name="OpenPositions">Symbols held now.</param>
/// <param name="OrdersToday">Orders created in the current session.</param>
public sealed record PortfolioRisk(
    decimal Equity,
    decimal EquityPeak,
    DateTimeOffset? PeakAt,
    decimal DrawdownPercent,
    int OpenPositions,
    int OrdersToday,
    RiskLimits Limits)
{
    public bool DrawdownLimitReached => DrawdownPercent >= Limits.MaxDrawdownPercent;

    public bool PositionLimitReached => OpenPositions >= Limits.MaxOpenPositions;

    public bool OrderLimitReached => OrdersToday >= Limits.MaxOrdersPerDay;

    /// <summary>The context the risk policy checks an order on <paramref name="symbol"/> against.</summary>
    public RiskContext ContextFor(decimal currentPositionQuantity, BrokerAccount account, int openOrders) =>
        new(currentPositionQuantity, account.DailyProfitLoss, openOrders, account.BuyingPower, OpenPositions, Equity, EquityPeak, OrdersToday);
}

/// <summary>
/// Measures the account against the portfolio limits and moves the high-water mark up when equity makes a new high.
/// Shared by the order checks, the proposals and the periodic guard, so they all use the same peak.
/// </summary>
internal sealed class PortfolioRiskReader(IEquityPeakStore peaks, IOrderRepository orders, IRiskLimitsStore limits, TimeProvider clock)
{
    public async Task<PortfolioRisk> ReadAsync(BrokerAccount account, IReadOnlyList<BrokerPosition> positions, CancellationToken cancellationToken)
    {
        var stored = await peaks.GetAsync(account.AccountNumber, cancellationToken).ConfigureAwait(false);
        var peak = stored;
        if (account.Equity > 0 && (stored is null || account.Equity > stored.Equity))
        {
            peak = new EquityPeak(account.AccountNumber, account.Equity, clock.GetUtcNow());
            await peaks.SaveAsync(peak, cancellationToken).ConfigureAwait(false);
        }

        var ordersToday = await orders.CountCreatedSinceAsync(ExchangeCalendar.StartOfToday(clock), cancellationToken).ConfigureAwait(false);
        var peakEquity = peak?.Equity ?? account.Equity;

        return new PortfolioRisk(
            account.Equity,
            peakEquity,
            peak?.At,
            RiskPolicy.DrawdownPercent(account.Equity, peakEquity) ?? 0m,
            positions.Count(p => p.Quantity != 0),
            ordersToday,
            limits.Current);
    }
}

/// <summary>The account against the portfolio limits, for the trading page.</summary>
public sealed record GetPortfolioRiskQuery : IQuery<PortfolioRisk>;

internal sealed class GetPortfolioRiskQueryHandler(IBrokerGateway broker, PortfolioRiskReader reader) : IQueryHandler<GetPortfolioRiskQuery, PortfolioRisk>
{
    public async Task<Result<PortfolioRisk>> HandleAsync(GetPortfolioRiskQuery query, CancellationToken cancellationToken)
    {
        try
        {
            var account = await broker.GetAccountAsync(cancellationToken);
            var positions = await broker.GetPositionsAsync(cancellationToken);
            return await reader.ReadAsync(account, positions, cancellationToken);
        }
        catch (BrokerException ex)
        {
            return TradingErrors.BrokerUnavailable(ex.Message);
        }
    }
}

/// <summary>
/// Sent periodically: records a new equity high, and when equity has fallen from it by the drawdown limit switches
/// trading off (the kill switch) and, if the limits say so, closes every position.
/// </summary>
/// <returns>Whether trading was halted by this check.</returns>
public sealed record EnforcePortfolioLimitsCommand : ICommand<bool>;

internal sealed class EnforcePortfolioLimitsCommandHandler(
    IBrokerGateway broker,
    PortfolioRiskReader reader,
    IRiskLimitsStore limits,
    IDispatcher dispatcher,
    Audit audit,
    INotifier notifier) : ICommandHandler<EnforcePortfolioLimitsCommand, bool>
{
    public async Task<Result<bool>> HandleAsync(EnforcePortfolioLimitsCommand command, CancellationToken cancellationToken)
    {
        PortfolioRisk risk;
        try
        {
            var account = await broker.GetAccountAsync(cancellationToken);
            var positions = await broker.GetPositionsAsync(cancellationToken);
            risk = await reader.ReadAsync(account, positions, cancellationToken);
        }
        catch (BrokerException ex)
        {
            return TradingErrors.BrokerUnavailable(ex.Message);
        }

        var current = limits.Current;
        if (!current.TradingEnabled || !risk.DrawdownLimitReached)
            return false;

        limits.Update(current with { TradingEnabled = false });
        await audit.RecordAsync(AuditCategory.Risk, "Halted",
            $"Equity {risk.Equity:N2} USD is {risk.DrawdownPercent:N2}% below its peak of {risk.EquityPeak:N2} USD; " +
            $"the limit is {current.MaxDrawdownPercent:N1}%. Trading switched off" + (current.FlattenOnDrawdown ? " and every position is being closed." : "."));
        notifier.Notify(new Notice(NoticeTopic.HaltedOnDrawdown, [risk.DrawdownPercent, current.MaxDrawdownPercent]));

        if (current.FlattenOnDrawdown)
        {
            var closed = await dispatcher.SendAsync(new CloseAllPositionsCommand(), cancellationToken);
            if (closed.IsFailure)
                await audit.RecordAsync(AuditCategory.Risk, "FlattenFailed", closed.Error.Message);
        }

        return true;
    }
}
