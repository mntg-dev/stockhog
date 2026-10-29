using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Stockhog.Application.Abstractions.Messaging;
using Stockhog.Application.Abstractions.Results;
using Stockhog.Application.Trading;
using Stockhog.Application.Trading.Orders;
using Stockhog.Application.Trading.Safety;
using Stockhog.Domain.MarketData;
using Stockhog.Domain.Trading;
using Stockhog.Presentation.Common;
using Stockhog.Presentation.Localization;
using Stockhog.Presentation.Settings;
using Stockhog.Presentation.Shell;
using Stockhog.Wpf.Controls.Charts;
using Microsoft.Extensions.Logging;

namespace Stockhog.Presentation.Trading;

/// <summary>
/// Paper trading: live watchlist and minute candles, order ticket with risk checks, positions and orders.
/// Live data and order events arrive on background threads; they only request a refresh, which
/// <see cref="ThrottledAction"/> runs on the UI thread at most a few times per second.
/// </summary>
public sealed partial class TradingViewModel : PageViewModel, IRecipient<PrefillOrderTicketMessage>
{
    private static readonly char[] SymbolSeparators = [',', ';', ' '];
    private static readonly TimeSpan LiveRefreshInterval = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan AccountRefreshInterval = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan PositionPollInterval = TimeSpan.FromSeconds(10);

    private readonly IDispatcher _dispatcher;
    private readonly ILiveMarketData _live;
    private readonly IQuoteCache _quotes;
    private readonly IConnectionMonitor _connections;
    private readonly IUserConfirmation _confirmation;
    private readonly ThrottledAction _liveRefresh;
    private readonly ThrottledAction _accountRefresh;

    public TradingViewModel(
        IDispatcher dispatcher,
        ILiveMarketData live,
        IQuoteCache quotes,
        IConnectionMonitor connections,
        TradingActivity activity,
        IUserConfirmation confirmation,
        IMessenger messenger,
        StatusViewModel status,
        UserSettings settings,
        ILogger<TradingViewModel> logger)
        : base(status, logger)
    {
        messenger.RegisterAll(this);
        _dispatcher = dispatcher;
        _live = live;
        _quotes = quotes;
        _connections = connections;
        _confirmation = confirmation;
        _watchlistText = string.Join(", ", settings.DefaultSymbols.Split(SymbolSeparators, StringSplitOptions.RemoveEmptyEntries).Append("SPY").Distinct());

        var ui = SynchronizationContext.Current;
        _liveRefresh = new ThrottledAction(LiveRefreshInterval, ApplyLiveData, ui);
        _accountRefresh = new ThrottledAction(AccountRefreshInterval, () => _ = RefreshAccountAsync(), ui);

        live.Updated += (_, _) => _liveRefresh.Request();
        connections.Changed += (_, _) => _liveRefresh.Request();
        activity.Changed += (_, _) => _accountRefresh.Request();
        Ticket.PropertyChanged += OnTicketChanged;
    }

    public OrderTicket Ticket { get; } = new();

    /// <summary>The assistant proposed an order: fill in the ticket and show its price. Nothing is sent.</summary>
    public void Receive(PrefillOrderTicketMessage message)
    {
        Ticket.Apply(message.Proposal);
        SelectedSymbol = message.Proposal.Symbol;
        Status.Show(Loc.Format("Status_TicketProposed", message.Proposal.Symbol));
    }

    // Watchlist and chart
    [ObservableProperty] private string _watchlistText;
    [ObservableProperty] private IReadOnlyList<Quote> _watchlist = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ClosePositionCommand))]
    private string? _selectedSymbol;

    [ObservableProperty] private IReadOnlyList<PriceBar> _bars = [];
    [ObservableProperty] private IReadOnlyList<ConnectionStatus> _connectionStates = [];

    // Account
    [ObservableProperty] private BrokerAccount? _account;
    [ObservableProperty] private IReadOnlyList<BrokerPosition> _positions = [];
    [ObservableProperty] private IReadOnlyList<OrderSummary> _orders = [];
    [ObservableProperty] private bool _openOrdersOnly = true;
    [ObservableProperty] private string? _accountError;

    // Risk and safety
    [ObservableProperty] private RiskLimits? _riskLimits;
    [ObservableProperty] private TradingSessionState? _session;
    [ObservableProperty] private IReadOnlyList<AuditEntry> _auditLog = [];

    public bool IsLive => Session?.Mode == TradingMode.Live;

    /// <summary>Live confirmations say plainly that real money is at stake.</summary>
    private string ConfirmTitle => IsLive ? Loc.Get("Ticket_ConfirmTitleLive") : Loc.Get("Ticket_ConfirmTitle");
    public bool IsArmed => Session?.IsArmed == true;

    public string EquityText => Account?.Equity.ToString("N2", Loc.Instance.Culture) ?? "–";
    public string DayProfitLossText => Account is { } a ? $"{a.DailyProfitLoss.ToString("+#,0.00;-#,0.00;0.00", Loc.Instance.Culture)} ({a.DailyProfitLossPercent:+0.00;-0.00;0.00}%)" : "–";
    public string BuyingPowerText => Account?.BuyingPower.ToString("N2", Loc.Instance.Culture) ?? "–";
    public string CashText => Account?.Cash.ToString("N2", Loc.Instance.Culture) ?? "–";
    public bool IsPaper => !IsLive;

    public bool TradingEnabled
    {
        get => RiskLimits?.TradingEnabled ?? false;
        set => _ = SetTradingEnabledAsync(value);
    }

    partial void OnAccountChanged(BrokerAccount? value)
    {
        OnPropertyChanged(nameof(EquityText));
        OnPropertyChanged(nameof(DayProfitLossText));
        OnPropertyChanged(nameof(BuyingPowerText));
        OnPropertyChanged(nameof(CashText));
    }

    partial void OnRiskLimitsChanged(RiskLimits? value) => OnPropertyChanged(nameof(TradingEnabled));

    partial void OnSessionChanged(TradingSessionState? value)
    {
        OnPropertyChanged(nameof(IsLive));
        OnPropertyChanged(nameof(IsArmed));
        OnPropertyChanged(nameof(IsPaper));
    }

    partial void OnOpenOrdersOnlyChanged(bool value) => _accountRefresh.Request();

    partial void OnSelectedSymbolChanged(string? value)
    {
        if (value is not null)
            Ticket.Symbol = value;
        ApplyLiveData();
    }

    protected override async Task LoadAsync()
    {
        await ApplyWatchlistAsync(CancellationToken.None);
        await RefreshAccountAsync();
        _ = PollAccountAsync();
    }

    /// <summary>Position values follow the market: refresh them now and then, even without order activity.</summary>
    private async Task PollAccountAsync()
    {
        using var timer = new PeriodicTimer(PositionPollInterval);
        while (await timer.WaitForNextTickAsync())
            await RefreshAccountAsync();
    }

    [RelayCommand]
    private async Task ApplyWatchlistAsync(CancellationToken cancellationToken)
    {
        var symbols = WatchlistText
            .Split(SymbolSeparators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => s.ToUpperInvariant())
            .Where(Symbol.IsValid)
            .Distinct()
            .Select(Symbol.Create)
            .ToList();

        try
        {
            await _live.SetWatchlistAsync(symbols, cancellationToken);
            SelectedSymbol ??= symbols.FirstOrDefault()?.Value;
            Status.Show(Loc.Format("Status_WatchlistSet", symbols.Count));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Status.Show(Loc.Format("Status_Failed", ex.Message));
        }
    }

    [RelayCommand]
    private async Task RefreshAccountAsync()
    {
        // The account, positions and orders are independent: load them at the same time.
        var accountTask = _dispatcher.SendAsync(new GetAccountQuery());
        var positionsTask = _dispatcher.SendAsync(new GetPositionsQuery());
        var ordersTask = _dispatcher.SendAsync(new GetOrdersQuery(OpenOrdersOnly));
        var auditTask = _dispatcher.SendAsync(new GetAuditLogQuery());
        await Task.WhenAll(accountTask, positionsTask, ordersTask, auditTask);

        // The daily loss guard may have switched trading off in the background.
        RiskLimits = (await _dispatcher.SendAsync(new GetRiskLimitsQuery())).Value;
        Session = (await _dispatcher.SendAsync(new GetTradingSessionQuery())).Value;
        if (await auditTask is { IsSuccess: true } audit)
            AuditLog = audit.Value;

        var account = await accountTask;
        Account = account.IsSuccess ? account.Value : null;
        AccountError = account.IsFailure ? account.Error.Message : null;
        Positions = (await positionsTask) is { IsSuccess: true } positions ? positions.Value.Positions : [];
        if (await ordersTask is { IsSuccess: true } orders)
            Orders = orders.Value;
    }

    private bool CanPlaceOrder() => !IsBusy;

    protected override void OnBusyChanged() => PlaceOrderCommand.NotifyCanExecuteChanged();

    [RelayCommand(CanExecute = nameof(CanPlaceOrder))]
    private async Task PlaceOrderAsync()
    {
        if (!Ticket.TryCreateCommand(out var command))
        {
            Status.Show(Ticket.Error ?? Loc.Get("Status_FixInputs"));
            return;
        }

        if (!_confirmation.Confirm(ConfirmTitle, Ticket.Describe(command)))
            return;

        await RunOperationAsync(Loc.Format("Status_PlacingOrder", command.Symbol), async _ =>
        {
            var placed = await _dispatcher.SendAsync(command, CancellationToken.None);
            await RefreshAccountAsync();
            return placed.IsSuccess
                ? Loc.Format("Status_OrderPlaced", placed.Value.ClientOrderId, Loc.Get($"OrderStatus_{placed.Value.Status}"))
                : Describe(placed.Error);
        });
    }

    [RelayCommand]
    private async Task CancelOrderAsync(OrderSummary? order)
    {
        if (order is null)
            return;

        var result = await _dispatcher.SendAsync(new CancelOrderCommand(order.ClientOrderId));
        Status.Show(result.IsSuccess ? Loc.Format("Status_CancelRequested", order.Symbol) : Describe(result.Error));
        await RefreshAccountAsync();
    }

    private bool CanClosePosition(string? symbol) => (symbol ?? SelectedSymbol) is not null;

    [RelayCommand(CanExecute = nameof(CanClosePosition))]
    private async Task ClosePositionAsync(string? symbol)
    {
        symbol ??= SelectedSymbol;
        if (symbol is null || !_confirmation.Confirm(ConfirmTitle, Loc.Format("Trading_ConfirmClose", symbol)))
            return;

        await RunOperationAsync(Loc.Format("Status_ClosingPosition", symbol), async _ =>
        {
            var result = await _dispatcher.SendAsync(new ClosePositionCommand(symbol));
            await RefreshAccountAsync();
            return result.IsSuccess ? Loc.Format("Status_PositionClosing", symbol) : Describe(result.Error);
        });
    }

    [RelayCommand]
    private async Task CloseAllAsync()
    {
        if (!_confirmation.Confirm(ConfirmTitle, Loc.Get("Trading_ConfirmCloseAll")))
            return;

        await RunOperationAsync(Loc.Get("Status_ClosingAll"), async _ =>
        {
            var result = await _dispatcher.SendAsync(new CloseAllPositionsCommand());
            await RefreshAccountAsync();
            if (result.IsFailure)
                return Describe(result.Error);

            return result.Value.Failures.Count == 0
                ? Loc.Format("Status_ClosedAll", result.Value.Orders.Count)
                : Loc.Format("Status_ClosedAllWithFailures", result.Value.Orders.Count, string.Join("; ", result.Value.Failures));
        });
    }

    /// <summary>Live only: the user types the account number to allow orders for this session.</summary>
    [RelayCommand]
    private async Task ArmLiveTradingAsync()
    {
        var typed = _confirmation.Prompt(Loc.Get("Trading_ArmTitle"), Loc.Format("Trading_ArmMessage", Account?.AccountNumber ?? "?"));
        if (typed is null)
            return;

        var result = await _dispatcher.SendAsync(new ArmLiveTradingCommand(typed));
        Status.Show(result.IsSuccess ? Loc.Get("Status_LiveArmed") : Describe(result.Error));
        await RefreshAccountAsync();
    }

    [RelayCommand]
    private async Task DisarmLiveTradingAsync()
    {
        await _dispatcher.SendAsync(new DisarmLiveTradingCommand());
        Status.Show(Loc.Get("Status_LiveDisarmed"));
        await RefreshAccountAsync();
    }

    private async Task SetTradingEnabledAsync(bool enabled)
    {
        if (RiskLimits is not { } limits || limits.TradingEnabled == enabled)
            return;

        var result = await _dispatcher.SendAsync(new UpdateRiskLimitsCommand(limits with { TradingEnabled = enabled }));
        if (result.IsSuccess)
            RiskLimits = result.Value;
        Status.Show(Loc.Get(enabled ? "Status_TradingEnabled" : "Status_TradingDisabled"));
    }

    private void OnTicketChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(OrderTicket.Symbol))
            UpdateTicketPrice();
    }

    /// <summary>Runs on the UI thread, at most every <see cref="LiveRefreshInterval"/>.</summary>
    private void ApplyLiveData()
    {
        Watchlist = _live.Watchlist
            .Select(symbol => _quotes.TryGet(Symbol.Create(symbol), out var quote) ? quote : new Quote(symbol, null, null, null, default))
            .ToList();
        ConnectionStates = _connections.GetAll();

        Bars = SelectedSymbol is { } selected && Symbol.IsValid(selected)
            ? _live.GetBars(Symbol.Create(selected))
                .Select(b => new PriceBar(DateTime.SpecifyKind(b.TimeUtc, DateTimeKind.Utc).ToLocalTime(), TimeSpan.FromMinutes(1),
                    (double)b.Open, (double)b.High, (double)b.Low, (double)b.Close))
                .ToList()
            : [];

        UpdateTicketPrice();
    }

    private void UpdateTicketPrice()
    {
        var symbol = Ticket.Symbol.Trim().ToUpperInvariant();
        Ticket.LivePrice = Symbol.IsValid(symbol) && _quotes.TryGet(Symbol.Create(symbol), out var quote) ? quote.Price : null;
    }

    private static string Describe(Error error) =>
        error.Details.Count > 0 ? string.Join(" ", error.Details) : error.Message;
}
