using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Stockhog.Application.Assistant;
using Stockhog.Application.Trading.Orders;
using Stockhog.Domain.Trading;
using Stockhog.Presentation.Localization;

namespace Stockhog.Presentation.Trading;

/// <summary>
/// The order entry form. Shows only the price fields the order type uses, estimates the order's value from the
/// live price, and turns the inputs into a <see cref="PlaceOrderCommand"/>.
/// </summary>
public sealed partial class OrderTicket : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Error))]
    private string _symbol = "AAPL";

    [ObservableProperty] private OrderSide _side = OrderSide.Buy;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasLimitPrice), nameof(HasStopPrice), nameof(Error), nameof(EstimatedValue))]
    private OrderType _type = OrderType.Market;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Error), nameof(EstimatedValue))]
    private string _quantity = "1";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Error), nameof(EstimatedValue))]
    private string _limitPrice = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Error), nameof(EstimatedValue))]
    private string _stopPrice = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Error))]
    private TimeInForce _timeInForce = TimeInForce.Day;

    /// <summary>Latest price of <see cref="Symbol"/>, set by the page from the live quotes.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EstimatedValue))]
    private decimal? _livePrice;

    /// <summary>Set when the assistant filled in the ticket: why it proposed the order. Cleared by the next proposal.</summary>
    [ObservableProperty] private string? _proposalNote;

    /// <summary>Fills in the ticket from the assistant's proposal. Nothing is sent: the user reviews and submits it.</summary>
    public void Apply(OrderProposal proposal)
    {
        Symbol = proposal.Symbol;
        Side = proposal.Side;
        Type = proposal.Type;
        Quantity = proposal.Quantity.ToString(CultureInfo.InvariantCulture);
        LimitPrice = proposal.LimitPrice?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
        StopPrice = string.Empty;
        TimeInForce = TimeInForce.Day;
        ProposalNote = proposal.Reason;
    }

    public IReadOnlyList<OrderSide> Sides { get; } = Enum.GetValues<OrderSide>();

    public IReadOnlyList<OrderType> Types { get; } = Enum.GetValues<OrderType>();

    public IReadOnlyList<TimeInForce> TimesInForce { get; } = Enum.GetValues<TimeInForce>();

    public bool HasLimitPrice => Type is OrderType.Limit or OrderType.StopLimit;

    public bool HasStopPrice => Type is OrderType.Stop or OrderType.StopLimit;

    /// <summary>Quantity times the limit, stop or live price; <c>null</c> while any of them is unknown.</summary>
    public decimal? EstimatedValue =>
        TryParse(Quantity, out var quantity) && ReferencePrice is { } price ? Math.Round(quantity * price, 2) : null;

    private decimal? ReferencePrice =>
        HasLimitPrice && TryParse(LimitPrice, out var limit) ? limit
        : HasStopPrice && TryParse(StopPrice, out var stop) ? stop
        : LivePrice;

    /// <summary>The first problem with the inputs, or <c>null</c>. The command validator checks again.</summary>
    public string? Error
    {
        get
        {
            if (!Domain.MarketData.Symbol.IsValid(Symbol.Trim().ToUpperInvariant()))
                return Loc.Get("Ticket_InvalidSymbol");
            if (!TryParse(Quantity, out var quantity) || quantity <= 0)
                return Loc.Get("Ticket_InvalidQuantity");
            if (quantity != decimal.Truncate(quantity) && (Type != OrderType.Market || TimeInForce != TimeInForce.Day))
                return Loc.Get("Ticket_FractionalMarketOnly");
            if (HasLimitPrice && (!TryParse(LimitPrice, out var limit) || limit <= 0))
                return Loc.Get("Ticket_InvalidLimit");
            if (HasStopPrice && (!TryParse(StopPrice, out var stop) || stop <= 0))
                return Loc.Get("Ticket_InvalidStop");
            return null;
        }
    }

    public bool TryCreateCommand([NotNullWhen(true)] out PlaceOrderCommand? command)
    {
        command = null;
        if (Error is not null)
            return false;

        TryParse(Quantity, out var quantity);
        command = new PlaceOrderCommand(
            Symbol.Trim().ToUpperInvariant(),
            Side,
            Type,
            quantity,
            HasLimitPrice && TryParse(LimitPrice, out var limit) ? limit : null,
            HasStopPrice && TryParse(StopPrice, out var stop) ? stop : null,
            TimeInForce);
        return true;
    }

    /// <summary>A sentence for the confirmation dialog, e.g. "Buy 10 AAPL, limit 180.00, day".</summary>
    public string Describe(PlaceOrderCommand command)
    {
        var culture = Loc.Instance.Culture;
        var prices = string.Join(", ", new[]
        {
            command.LimitPrice is { } limit ? Loc.Format("Ticket_DescribeLimit", limit.ToString("N2", culture)) : null,
            command.StopPrice is { } stop ? Loc.Format("Ticket_DescribeStop", stop.ToString("N2", culture)) : null
        }.Where(p => p is not null));

        return Loc.Format(
            "Ticket_Describe",
            Loc.Get($"OrderSide_{command.Side}"),
            command.Quantity.ToString("0.#########", culture),
            command.Symbol,
            Loc.Get($"OrderType_{command.Type}"),
            prices.Length == 0 ? string.Empty : $" ({prices})",
            Loc.Get($"TimeInForce_{command.TimeInForce}"),
            EstimatedValue is { } value ? value.ToString("N2", culture) : "?");
    }

    private static bool TryParse(string text, out decimal value) =>
        decimal.TryParse(text.Trim().Replace(',', '.'), NumberStyles.Number, CultureInfo.InvariantCulture, out value);
}
