using Stockhog.Application.Abstractions.Messaging;
using Stockhog.Application.Abstractions.Persistence;
using Stockhog.Application.Trading;
using Stockhog.Application.Trading.Orders;
using Stockhog.Application.Trading.Safety;
using Stockhog.Infrastructure.Alpaca.Streaming;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Stockhog.Infrastructure.Alpaca.Trading;

/// <summary>
/// The account's order events (new, fill, partial_fill, canceled, rejected, expired, …). Each is applied to the
/// matching order; after every (re)connect the orders are reconciled, so events missed while disconnected are caught up.
/// </summary>
internal sealed class AlpacaTradeUpdatesStream(
    IDispatcher dispatcher,
    IOptions<AlpacaOptions> options,
    StreamSocketFactory socketFactory,
    ConnectionMonitor monitor,
    TimeProvider clock,
    ILogger<AlpacaTradeUpdatesStream> logger) : AlpacaStreamService(socketFactory, monitor, clock, logger)
{
    public const string StreamName = "Trade updates";

    private static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(15);
    private const int MaxApplyAttempts = 3;

    protected override string Name => StreamName;

    protected override Uri Endpoint => new(options.Value.TradingStreamUrl);

    protected override bool HasCredentials => options.Value.HasTradingCredentials;

    protected override async Task HandshakeAsync(IStreamSocket socket, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(HandshakeTimeout);

        var settings = options.Value;
        await socket.SendAsync(StreamJson.Serialize(new { action = "auth", key = settings.TradingKey, secret = settings.TradingSecret }), timeout.Token);
        var authorization = await ExpectAsync(socket, "authorization", timeout.Token);
        if (authorization.TryGetProperty("status", out var status) && status.GetString() != "authorized")
            throw new StreamAuthenticationException("The trading stream refused the keys; are they paper trading keys?");

        await socket.SendAsync(StreamJson.Serialize(new { action = "listen", data = new { streams = new[] { "trade_updates" } } }), timeout.Token);
        await ExpectAsync(socket, "listening", timeout.Token);
    }

    /// <summary>Failures here are logged, not thrown: they must not tear down a healthy connection.</summary>
    protected override async Task OnConnectedAsync(CancellationToken cancellationToken)
    {
        try
        {
            var result = await dispatcher.SendAsync(new ReconcileOrdersCommand(), cancellationToken);
            if (result is { IsSuccess: true, Value.Changed: > 0 })
                logger.LogInformation("Caught up on {Changed} order change(s) after connecting", result.Value.Changed);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Reconciliation after connecting failed; the periodic one will retry");
        }
    }

    protected override async Task OnMessageAsync(string message, CancellationToken cancellationToken)
    {
        var (stream, data) = AlpacaStreamMessages.ParseTradingEnvelope(message);
        if (stream != "trade_updates" || AlpacaStreamMessages.ParseTradeUpdate(data) is not { } update)
            return;

        logger.LogInformation("Trade update {Event}: {Symbol} {ClientOrderId} filled {Filled}/{Quantity}",
            update.Event, update.Order.Symbol, update.Order.ClientOrderId, update.Order.FilledQuantity, update.Order.Quantity);

        try
        {
            // A conflict means the order was saved elsewhere meanwhile (e.g. by the submitting handler): each attempt
            // runs in a new scope and reloads the order, so applying the update again is safe.
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    var result = await dispatcher.SendAsync(new ApplyTradeUpdateCommand(update), cancellationToken);
                    if (result.IsFailure)
                        logger.LogWarning("Trade update for {ClientOrderId} not applied: {Error}", update.Order.ClientOrderId, result.Error.Message);
                    break;
                }
                catch (ConcurrencyConflictException) when (attempt < MaxApplyAttempts)
                {
                    logger.LogInformation("Trade update for {ClientOrderId} conflicted with another change; retrying", update.Order.ClientOrderId);
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Reconciliation will pick the change up from the broker.
            logger.LogError(ex, "Trade update for {ClientOrderId} failed", update.Order.ClientOrderId);
        }
    }

    private static async Task<System.Text.Json.JsonElement> ExpectAsync(IStreamSocket socket, string stream, CancellationToken cancellationToken)
    {
        while (await socket.ReceiveAsync(cancellationToken) is { } message)
        {
            var (name, data) = AlpacaStreamMessages.ParseTradingEnvelope(message);
            if (name == stream)
                return data;
        }

        throw new IOException($"The trading stream closed while waiting for '{stream}'.");
    }
}

/// <summary>
/// Compares working orders with the broker on a timer, as a safety net for stream events that never arrived.
/// </summary>
internal sealed class OrderReconciliationService(
    IDispatcher dispatcher,
    IOptions<AlpacaOptions> options,
    TimeProvider clock,
    ILogger<OrderReconciliationService> logger) : Microsoft.Extensions.Hosting.BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.HasTradingCredentials)
            return;

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(options.Value.ReconcileIntervalSeconds), clock);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    var result = await dispatcher.SendAsync(new ReconcileOrdersCommand(), stoppingToken);
                    if (result is { IsSuccess: true, Value.Changed: > 0 })
                        logger.LogInformation("Reconciliation updated {Changed} of {Checked} order(s)", result.Value.Changed, result.Value.Checked);
                }
                catch (BrokerException ex)
                {
                    logger.LogWarning("Reconciliation skipped: {Reason}", ex.Message);
                }
                catch (ConcurrencyConflictException ex)
                {
                    // An order changed while it was being reconciled; the next round compares it again.
                    logger.LogInformation("Reconciliation deferred: {Reason}", ex.Message);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutting down.
        }
    }
}

/// <summary>
/// Checks today's loss against the limit every minute and switches trading off once it is reached, so the halt
/// happens even if nobody tries to place an order.
/// </summary>
internal sealed class DailyLossGuardService(
    IDispatcher dispatcher,
    IOptions<AlpacaOptions> options,
    TimeProvider clock,
    ILogger<DailyLossGuardService> logger) : Microsoft.Extensions.Hosting.BackgroundService
{
    internal static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.HasTradingCredentials)
            return;

        using var timer = new PeriodicTimer(Interval, clock);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                var result = await dispatcher.SendAsync(new EnforceDailyLossLimitCommand(), stoppingToken);
                if (result is { IsSuccess: true, Value: true })
                    logger.LogWarning("Daily loss limit reached: trading switched off");
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutting down.
        }
    }
}
