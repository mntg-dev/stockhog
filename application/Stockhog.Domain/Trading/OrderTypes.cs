using System.Globalization;
using System.Security.Cryptography;
using Stockhog.Domain.Common;

namespace Stockhog.Domain.Trading;

/// <summary>Which account orders go to. Chosen at startup; live trading needs explicit opt-in.</summary>
public enum TradingMode
{
    Paper,
    Live
}

public enum OrderSide
{
    Buy,
    Sell
}

public enum OrderType
{
    Market,
    Limit,
    Stop,
    StopLimit
}

public enum TimeInForce
{
    /// <summary>Cancelled at the end of the trading day if not filled.</summary>
    Day,

    /// <summary>Good until cancelled.</summary>
    Gtc
}

/// <summary>
/// <code>
/// New --Submit--> Submitted --fill--> PartiallyFilled --fill--> Filled
///  |                 |                     |
///  '--Reject         '--Cancel/Reject/Expire '--Cancel/Expire (keeps the filled part)
/// </code>
/// Filled, Cancelled, Rejected and Expired are final.
/// </summary>
public enum OrderStatus
{
    New,
    Submitted,
    PartiallyFilled,
    Filled,
    Cancelled,
    Rejected,
    Expired
}

/// <summary>
/// The app's own id of an order, sent to the broker as <c>client_order_id</c>. The broker refuses a second order
/// with the same id, so a submit that is repeated after a timeout can never create two orders.
/// </summary>
public sealed record ClientOrderId
{
    public const int MaxLength = 48;
    private const string Prefix = "emt-";

    private ClientOrderId(string value)
    {
        Value = value;
    }

    public string Value { get; }

    /// <summary>A sortable id with a random suffix, e.g. <c>emt-20261019-101500123-3f9a1c2b</c>.</summary>
    public static ClientOrderId New(DateTimeOffset createdAt) =>
        new($"{Prefix}{createdAt.ToString("yyyyMMdd-HHmmssfff", CultureInfo.InvariantCulture)}-{Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(4))}");

    public static ClientOrderId From(string value) =>
        IsValid(value) ? new ClientOrderId(value) : throw new DomainException($"'{value}' is not a valid client order id.");

    public static bool IsValid(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && value.Length <= MaxLength
        && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

    public override string ToString() => Value;
}
