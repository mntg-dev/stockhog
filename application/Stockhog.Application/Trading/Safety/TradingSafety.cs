using Stockhog.Application.Abstractions.Events;
using Stockhog.Application.Abstractions.Messaging;
using Stockhog.Application.Abstractions.Results;
using Stockhog.Domain.Trading;
using FluentValidation;

namespace Stockhog.Application.Trading.Safety;

/// <summary>The account the broker adapter was started against.</summary>
public interface ITradingEnvironment
{
    TradingMode Mode { get; }
}

/// <summary>
/// Live orders are refused until the user arms live trading for this session by typing the account number.
/// Disarmed again when the app restarts. Paper trading needs no arming.
/// </summary>
public sealed class LiveTradingSession
{
    private int _armed;

    public bool IsArmed => Volatile.Read(ref _armed) == 1;

    internal void Arm() => Volatile.Write(ref _armed, 1);

    internal void Disarm() => Volatile.Write(ref _armed, 0);
}

public enum AuditCategory
{
    Order,
    Risk,
    Session
}

/// <summary>One line of the trading audit trail.</summary>
/// <param name="ClientOrderId">The order concerned, if any.</param>
public sealed record AuditEntry(
    DateTimeOffset Time,
    TradingMode Mode,
    AuditCategory Category,
    string Action,
    string? ClientOrderId,
    string? Symbol,
    string Details);

/// <summary>
/// Append-only record of every order action, risk refusal and safety change. Writing it must never stop trading:
/// implementations report their own failures instead of throwing.
/// </summary>
public interface IAuditLog
{
    Task RecordAsync(AuditEntry entry, CancellationToken cancellationToken = default);

    /// <returns>Newest first.</returns>
    Task<IReadOnlyList<AuditEntry>> ListAsync(int take, CancellationToken cancellationToken = default);
}

/// <summary>Creates entries stamped with the time and trading mode.</summary>
public sealed class Audit(IAuditLog log, ITradingEnvironment environment, TimeProvider clock)
{
    public Task RecordAsync(AuditCategory category, string action, string details, string? clientOrderId = null, string? symbol = null) =>
        log.RecordAsync(new AuditEntry(clock.GetUtcNow(), environment.Mode, category, action, clientOrderId, symbol, details), CancellationToken.None);
}

public static class SafetyErrors
{
    public static Error LiveNotArmed() =>
        new("Trading.LiveNotArmed", "Live trading is not armed for this session. Arm it on the trading page first.", ErrorType.Conflict);

    public static Error WrongConfirmation() =>
        Error.Validation(["The text does not match the account number; live trading stays disarmed."]);

    public static Error NotLive() =>
        new("Trading.NotLive", "The app runs against the paper account; there is nothing to arm.", ErrorType.Conflict);
}

public sealed record TradingSessionState(TradingMode Mode, bool IsArmed)
{
    /// <summary>Orders can be sent: paper always, live only once armed.</summary>
    public bool CanTrade => Mode == TradingMode.Paper || IsArmed;
}

public sealed record GetTradingSessionQuery : IQuery<TradingSessionState>;

internal sealed class GetTradingSessionQueryHandler(ITradingEnvironment environment, LiveTradingSession session)
    : IQueryHandler<GetTradingSessionQuery, TradingSessionState>
{
    public Task<Result<TradingSessionState>> HandleAsync(GetTradingSessionQuery query, CancellationToken cancellationToken) =>
        Task.FromResult(Result<TradingSessionState>.Success(new TradingSessionState(environment.Mode, session.IsArmed)));
}

/// <param name="Confirmation">Must equal the live account's number, as typed by the user.</param>
public sealed record ArmLiveTradingCommand(string Confirmation) : ICommand<TradingSessionState>;

internal sealed class ArmLiveTradingCommandValidator : AbstractValidator<ArmLiveTradingCommand>
{
    public ArmLiveTradingCommandValidator() => RuleFor(c => c.Confirmation).NotEmpty().WithMessage("Type the account number to arm live trading.");
}

internal sealed class ArmLiveTradingCommandHandler(
    IBrokerGateway broker,
    ITradingEnvironment environment,
    LiveTradingSession session,
    Audit audit) : ICommandHandler<ArmLiveTradingCommand, TradingSessionState>
{
    public async Task<Result<TradingSessionState>> HandleAsync(ArmLiveTradingCommand command, CancellationToken cancellationToken)
    {
        if (environment.Mode != TradingMode.Live)
            return SafetyErrors.NotLive();

        BrokerAccount account;
        try
        {
            account = await broker.GetAccountAsync(cancellationToken);
        }
        catch (BrokerException ex)
        {
            return TradingErrors.BrokerUnavailable(ex.Message);
        }

        if (!string.Equals(command.Confirmation.Trim(), account.AccountNumber, StringComparison.Ordinal))
        {
            await audit.RecordAsync(AuditCategory.Session, "ArmRefused", "Wrong confirmation text.");
            return SafetyErrors.WrongConfirmation();
        }

        session.Arm();
        await audit.RecordAsync(AuditCategory.Session, "Armed", $"Live trading armed for account {account.AccountNumber}.");
        return new TradingSessionState(environment.Mode, session.IsArmed);
    }
}

public sealed record DisarmLiveTradingCommand : ICommand<TradingSessionState>;

internal sealed class DisarmLiveTradingCommandHandler(ITradingEnvironment environment, LiveTradingSession session, Audit audit)
    : ICommandHandler<DisarmLiveTradingCommand, TradingSessionState>
{
    public async Task<Result<TradingSessionState>> HandleAsync(DisarmLiveTradingCommand command, CancellationToken cancellationToken)
    {
        if (session.IsArmed)
        {
            session.Disarm();
            await audit.RecordAsync(AuditCategory.Session, "Disarmed", "Live trading disarmed.");
        }

        return new TradingSessionState(environment.Mode, session.IsArmed);
    }
}

/// <summary>
/// Sent periodically: when today's loss reaches the limit, trading is switched off (the kill switch) so no new
/// exposure can be opened for the rest of the session. Positions can still be closed.
/// </summary>
/// <returns>Whether trading was halted by this check.</returns>
public sealed record EnforceDailyLossLimitCommand : ICommand<bool>;

internal sealed class EnforceDailyLossLimitCommandHandler(
    IBrokerGateway broker,
    IRiskLimitsStore limits,
    Audit audit) : ICommandHandler<EnforceDailyLossLimitCommand, bool>
{
    public async Task<Result<bool>> HandleAsync(EnforceDailyLossLimitCommand command, CancellationToken cancellationToken)
    {
        var current = limits.Current;
        if (!current.TradingEnabled)
            return false;

        BrokerAccount account;
        try
        {
            account = await broker.GetAccountAsync(cancellationToken);
        }
        catch (BrokerException ex)
        {
            return TradingErrors.BrokerUnavailable(ex.Message);
        }

        if (-account.DailyProfitLoss < current.MaxDailyLoss)
            return false;

        limits.Update(current with { TradingEnabled = false });
        await audit.RecordAsync(AuditCategory.Risk, "Halted",
            $"Daily loss {-account.DailyProfitLoss:N2} USD reached the limit of {current.MaxDailyLoss:N2} USD; trading switched off.");
        return true;
    }
}

public sealed record GetAuditLogQuery(int Take = 200) : IQuery<IReadOnlyList<AuditEntry>>;

internal sealed class GetAuditLogQueryValidator : AbstractValidator<GetAuditLogQuery>
{
    public GetAuditLogQueryValidator() => RuleFor(q => q.Take).InclusiveBetween(1, 5000);
}

internal sealed class GetAuditLogQueryHandler(IAuditLog log) : IQueryHandler<GetAuditLogQuery, IReadOnlyList<AuditEntry>>
{
    public async Task<Result<IReadOnlyList<AuditEntry>>> HandleAsync(GetAuditLogQuery query, CancellationToken cancellationToken) =>
        Result<IReadOnlyList<AuditEntry>>.Success(await log.ListAsync(query.Take, cancellationToken));
}

/// <summary>Every state change of every order goes into the audit trail.</summary>
internal sealed class AuditOrderEvents(Audit audit) :
    IDomainEventHandler<OrderCreated>,
    IDomainEventHandler<OrderSubmitted>,
    IDomainEventHandler<OrderPartiallyFilled>,
    IDomainEventHandler<OrderFilled>,
    IDomainEventHandler<OrderClosed>
{
    public Task HandleAsync(OrderCreated e, CancellationToken cancellationToken) =>
        audit.RecordAsync(AuditCategory.Order, "Created",
            $"{e.Side} {e.Quantity} {e.Symbol} {e.Type}" + (e.LimitPrice is { } l ? $" limit {l}" : "") + (e.StopPrice is { } s ? $" stop {s}" : ""),
            e.OrderId.Value, e.Symbol.Value);

    public Task HandleAsync(OrderSubmitted e, CancellationToken cancellationToken) =>
        audit.RecordAsync(AuditCategory.Order, "Submitted", "Accepted by the broker.", e.OrderId.Value, e.Symbol.Value);

    public Task HandleAsync(OrderPartiallyFilled e, CancellationToken cancellationToken) =>
        audit.RecordAsync(AuditCategory.Order, "PartiallyFilled", $"{e.FilledQuantity} filled at {e.AveragePrice} on average.", e.OrderId.Value, e.Symbol.Value);

    public Task HandleAsync(OrderFilled e, CancellationToken cancellationToken) =>
        audit.RecordAsync(AuditCategory.Order, "Filled", $"{e.Quantity} filled at {e.AveragePrice} on average.", e.OrderId.Value, e.Symbol.Value);

    public Task HandleAsync(OrderClosed e, CancellationToken cancellationToken) =>
        audit.RecordAsync(AuditCategory.Order, e.Status.ToString(), e.Reason ?? $"{e.FilledQuantity} filled before closing.", e.OrderId.Value, e.Symbol.Value);
}
