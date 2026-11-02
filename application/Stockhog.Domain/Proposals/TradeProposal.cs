using System.Globalization;
using Stockhog.Domain.Backtesting;
using Stockhog.Domain.Common;
using Stockhog.Domain.MarketData;
using Stockhog.Domain.Scanning;
using Stockhog.Domain.Signals;
using Stockhog.Domain.Trading;

namespace Stockhog.Domain.Proposals;

/// <summary>Readable, sortable id, e.g. <c>prop-20261102-201500123_SPY</c>.</summary>
public sealed record ProposalId
{
    private const string Prefix = "prop-";

    private ProposalId(string value)
    {
        Value = value;
    }

    public string Value { get; }

    public static ProposalId New(Symbol symbol, DateTimeOffset createdAt) =>
        new($"{Prefix}{createdAt.ToString("yyyyMMdd-HHmmssfff", CultureInfo.InvariantCulture)}_{symbol.Value}");

    public static ProposalId From(string value) =>
        IsValid(value) ? new ProposalId(value) : throw new DomainException($"'{value}' is not a valid proposal id.");

    public static bool IsValid(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && value.StartsWith(Prefix, StringComparison.Ordinal)
        && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.');

    public override string ToString() => Value;
}

public enum ProposalStatus
{
    /// <summary>Waiting for the user's decision.</summary>
    Pending,

    /// <summary>Approved and sent to the broker as an order.</summary>
    Submitted,

    Rejected,

    /// <summary>Not decided before the session it was meant for ended.</summary>
    Expired,

    /// <summary>Approved, but the order was refused (risk limits, the broker, a price that moved too far).</summary>
    Failed
}

/// <summary>
/// A buy the app suggests from a fresh strategy signal, sized by risk and with a broker-side stop, waiting for the
/// user to approve or reject it. Approving sends a normal order through every safety check; nothing is sent without it.
/// <code>
/// Pending --Submit--> Submitted
///    |--Reject------> Rejected
///    |--Expire------> Expired   (past ExpiresAt)
///    '--Fail--------> Failed    (approved, but the order could not be placed)
/// </code>
/// </summary>
public sealed class TradeProposal : AggregateRoot<ProposalId>
{
    /// <summary>The price may move this far from the reference before an approval is refused.</summary>
    public const decimal MaxPriceDeviationPercent = 3m;

    private TradeProposal(
        ProposalId id,
        Symbol symbol,
        StrategyName strategy,
        StrategySignal signal,
        decimal quantity,
        decimal referencePrice,
        decimal stopPrice,
        decimal riskAmount,
        decimal equity,
        decimal? score,
        ScanRunId? scanId,
        DateTimeOffset createdAt,
        DateTimeOffset expiresAt) : base(id)
    {
        Symbol = symbol;
        Strategy = strategy;
        Signal = signal;
        Quantity = quantity;
        ReferencePrice = referencePrice;
        StopPrice = stopPrice;
        RiskAmount = riskAmount;
        Equity = equity;
        Score = score;
        ScanId = scanId;
        CreatedAt = createdAt;
        ExpiresAt = expiresAt;
        Status = ProposalStatus.Pending;
    }

#pragma warning disable CS8618 // Persistence materializes proposals through this constructor and then sets every property.
    private TradeProposal() : base(default!)
    {
    }
#pragma warning restore CS8618

    public Symbol Symbol { get; }
    public StrategyName Strategy { get; }

    /// <summary>The fresh entry the proposal follows.</summary>
    public StrategySignal Signal { get; }

    /// <summary>Whole shares to buy.</summary>
    public decimal Quantity { get; }

    /// <summary>The last close the size was computed from.</summary>
    public decimal ReferencePrice { get; }

    /// <summary>The broker-side stop loss sent with the order.</summary>
    public decimal StopPrice { get; }

    /// <summary>Loss in USD if the stop is hit at the reference price.</summary>
    public decimal RiskAmount { get; }

    /// <summary>Account equity when the proposal was made.</summary>
    public decimal Equity { get; }

    /// <summary>The pair's score in the scan it came from, if any.</summary>
    public decimal? Score { get; }

    public ScanRunId? ScanId { get; }

    public DateTimeOffset CreatedAt { get; }
    public DateTimeOffset ExpiresAt { get; }

    public ProposalStatus Status { get; private set; }
    public DateTimeOffset? DecidedAt { get; private set; }

    /// <summary>The order sent on approval.</summary>
    public ClientOrderId? OrderId { get; private set; }

    /// <summary>Why it was rejected or failed.</summary>
    public string? Reason { get; private set; }

    public decimal Notional => Quantity * ReferencePrice;

    public decimal RiskPercentOfEquity => Equity <= 0 ? 0 : Math.Round(RiskAmount / Equity * 100m, 2);

    public bool IsPending => Status == ProposalStatus.Pending;

    public static TradeProposal Create(
        ProposalId id,
        Symbol symbol,
        StrategyName strategy,
        StrategySignal signal,
        PositionSize size,
        decimal equity,
        decimal? score,
        ScanRunId? scanId,
        DateTimeOffset createdAt,
        DateTimeOffset expiresAt)
    {
        if (signal.Kind != SignalKind.Entry)
            throw new DomainException($"Only a fresh entry can be proposed, not {signal.Kind}.");
        if (size.Quantity <= 0 || size.Quantity != decimal.Truncate(size.Quantity))
            throw new DomainException("A proposal needs a whole, positive number of shares.");
        if (signal.LastPrice <= 0 || size.StopPrice <= 0 || size.StopPrice >= signal.LastPrice)
            throw new DomainException("The stop must be positive and below the price.");
        if (expiresAt <= createdAt)
            throw new DomainException("A proposal must expire after it is created.");

        return new TradeProposal(id, symbol, strategy, signal, size.Quantity, signal.LastPrice, size.StopPrice, size.RiskAmount,
            equity, score, scanId, createdAt, expiresAt);
    }

    /// <summary>Change of <paramref name="livePrice"/> from the reference, in percent.</summary>
    public decimal PriceDeviationPercent(decimal livePrice) => Math.Round((livePrice - ReferencePrice) / ReferencePrice * 100m, 2);

    /// <summary>
    /// Why the proposal can no longer be approved at <paramref name="livePrice"/>, or <c>null</c> if it can: the price
    /// moved too far from the reference, or is already at the stop.
    /// </summary>
    public string? ApprovalProblem(decimal livePrice)
    {
        var invariant = CultureInfo.InvariantCulture;
        if (livePrice <= StopPrice)
            return string.Create(invariant, $"The price {livePrice:N2} is at or below the stop {StopPrice:N2}.");

        var deviation = PriceDeviationPercent(livePrice);
        return Math.Abs(deviation) > MaxPriceDeviationPercent
            ? string.Create(invariant, $"The price moved {deviation:+0.00;-0.00}% from {ReferencePrice:N2}; the limit is ±{MaxPriceDeviationPercent}%.")
            : null;
    }

    public bool IsExpiredAt(DateTimeOffset at) => at >= ExpiresAt;

    public void Submit(ClientOrderId orderId, DateTimeOffset at)
    {
        EnsurePending("submit");
        if (IsExpiredAt(at))
            throw new DomainException($"Proposal {Id} expired at {ExpiresAt:u}.");

        Status = ProposalStatus.Submitted;
        OrderId = orderId;
        DecidedAt = at;
    }

    public void Reject(string? reason, DateTimeOffset at)
    {
        EnsurePending("reject");
        Status = ProposalStatus.Rejected;
        Reason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim();
        DecidedAt = at;
    }

    /// <param name="orderId">The order that was created, if it got that far.</param>
    public void Fail(string reason, ClientOrderId? orderId, DateTimeOffset at)
    {
        EnsurePending("fail");
        Status = ProposalStatus.Failed;
        Reason = string.IsNullOrWhiteSpace(reason) ? "Unknown error" : reason.Trim();
        OrderId = orderId;
        DecidedAt = at;
    }

    /// <returns><c>true</c> if the proposal expired now.</returns>
    public bool ExpireIfDue(DateTimeOffset at)
    {
        if (!IsPending || !IsExpiredAt(at))
            return false;

        Status = ProposalStatus.Expired;
        DecidedAt = at;
        return true;
    }

    private void EnsurePending(string action)
    {
        if (!IsPending)
            throw new DomainException($"Cannot {action} proposal {Id}: it is {Status}.");
    }
}
