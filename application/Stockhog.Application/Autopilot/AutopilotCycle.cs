using System.Globalization;
using Stockhog.Application.Abstractions.Jobs;
using Stockhog.Application.Abstractions.Messaging;
using Stockhog.Application.Abstractions.Results;
using Stockhog.Application.Common;
using Stockhog.Application.Signals.EvaluateSignal;
using Stockhog.Application.Trading;
using Stockhog.Application.Trading.Orders;
using Stockhog.Application.Trading.Portfolio;
using Stockhog.Application.Trading.Safety;
using Stockhog.Domain.Autopilot;
using Stockhog.Domain.MarketData;
using Stockhog.Domain.Proposals;
using Stockhog.Domain.Signals;
using Stockhog.Domain.Trading;
using FluentValidation;

namespace Stockhog.Application.Autopilot;

/// <summary>
/// Runs one autopilot cycle as a background job: reads today's signal of every pair it follows, then decides for each.
/// <paramref name="Scheduled"/> cycles run only when due: autopilot on, a weekday during the session, not yet run for
/// today. A manual cycle runs whenever the autopilot is on.
/// </summary>
public sealed record QueueAutopilotCycleCommand(bool Scheduled = false) : ICommand<QueuedAutopilotCycle>;

/// <param name="JobId">The queued or running job; null when a scheduled cycle was not due.</param>
public sealed record QueuedAutopilotCycle(Guid? JobId, bool WasQueued, int Pairs);

/// <summary>
/// Decides for every pair from the signals the cycle job read: exits first, then entries by score, each sized and
/// checked against the risk limits and the autopilot's own limits. In shadow mode a decision only moves the simulated
/// position; no order is sent. Every decision is written to the decision log.
/// </summary>
/// <param name="Session">The New York day the cycle ran on.</param>
public sealed record ApplyAutopilotCycleCommand(DateOnly Session, IReadOnlyList<AssignmentSignal> Signals) : ICommand<AutopilotCycleSummary>;

/// <param name="Error">Why the signal could not be read; then <paramref name="Signal"/> is null.</param>
public sealed record AssignmentSignal(int AssignmentId, StrategySignal? Signal, string? Error = null);

public sealed record AutopilotCycleSummary(AutopilotMode Mode, int Buys, int Sells, int Holds, int Waits, int Skips)
{
    public override string ToString() =>
        $"{Mode} cycle: {Buys} buys, {Sells} sells, {Holds} holds, {Waits} waiting, {Skips} skipped.";
}

public static class AutopilotSchedule
{
    public const string CycleJobKey = "autopilot:cycle";

    /// <summary>Not before the opening auction has settled.</summary>
    public static readonly TimeOnly Opens = new(9, 35);

    /// <summary>Not so late that an order could miss the close.</summary>
    public static readonly TimeOnly Closes = new(15, 55);

    /// <summary>Whether a scheduled cycle should run at <paramref name="newYorkNow"/>.</summary>
    public static bool IsDue(Domain.Autopilot.Autopilot autopilot, DateTime newYorkNow)
    {
        var today = DateOnly.FromDateTime(newYorkNow);
        var time = TimeOnly.FromDateTime(newYorkNow);
        return autopilot.Mode != AutopilotMode.Off
               && newYorkNow.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday)
               && time >= Opens && time < Closes
               && autopilot.LastCycleSession != today;
    }
}

internal sealed class ApplyAutopilotCycleCommandValidator : AbstractValidator<ApplyAutopilotCycleCommand>
{
    public ApplyAutopilotCycleCommandValidator() => RuleFor(c => c.Signals).NotNull();
}

internal sealed class QueueAutopilotCycleCommandHandler(IAutopilotRepository autopilots, IJobQueue jobs, TimeProvider clock)
    : ICommandHandler<QueueAutopilotCycleCommand, QueuedAutopilotCycle>
{
    /// <summary>Signals are backtests; a few at a time keep the machine usable.</summary>
    private const int MaxParallel = 2;

    public async Task<Result<QueuedAutopilotCycle>> HandleAsync(QueueAutopilotCycleCommand command, CancellationToken cancellationToken)
    {
        var autopilot = await autopilots.GetOrCreateAsync(cancellationToken);
        var now = ExchangeCalendar.NewYorkNow(clock);
        if (command.Scheduled && !AutopilotSchedule.IsDue(autopilot, now))
            return new QueuedAutopilotCycle(null, false, 0);
        if (autopilot.Mode == AutopilotMode.Off)
            return AutopilotErrors.Off();

        // Pairs no longer followed still get their exits while they hold a position.
        var pairs = autopilot.Assignments
            .Where(a => a.IsActive || a.IsHolding)
            .Select(a => (a.Id, Symbol: a.Symbol.Value, Strategy: a.Strategy.Value))
            .ToList();
        if (pairs.Count == 0)
            return AutopilotErrors.NothingToFollow();

        var session = DateOnly.FromDateTime(now);
        var job = jobs.Enqueue($"Autopilot cycle ({pairs.Count} pairs)", context => RunAsync(session, pairs, context), AutopilotSchedule.CycleJobKey);
        return new QueuedAutopilotCycle(job.Job.Id, job.WasQueued, pairs.Count);
    }

    /// <summary>A signal that cannot be read becomes a skip for its pair; the others go on.</summary>
    private static async Task<JobOutcome> RunAsync(DateOnly session, IReadOnlyList<(int Id, string Symbol, string Strategy)> pairs, JobExecutionContext context)
    {
        var signals = new AssignmentSignal[pairs.Count];
        var done = 0;
        context.Progress.Report(new JobProgress(0, $"0/{pairs.Count} signals"));

        var parallel = new ParallelOptions { MaxDegreeOfParallelism = MaxParallel, CancellationToken = context.CancellationToken };
        await Parallel.ForEachAsync(Enumerable.Range(0, pairs.Count), parallel, async (i, token) =>
        {
            var pair = pairs[i];
            var signal = await context.Dispatcher.SendAsync(new EvaluateSignalCommand(pair.Strategy, pair.Symbol), token);
            signals[i] = signal.IsSuccess ? new AssignmentSignal(pair.Id, signal.Value) : new AssignmentSignal(pair.Id, null, signal.Error.Message);

            var finished = Interlocked.Increment(ref done);
            context.Progress.Report(new JobProgress(finished * 95 / pairs.Count, $"{finished}/{pairs.Count} signals"));
        });

        var applied = await context.Dispatcher.SendAsync(new ApplyAutopilotCycleCommand(session, signals), context.CancellationToken);
        return applied.Match(s => JobOutcome.Succeeded(s.ToString()), error => JobOutcome.Failed(error.Message));
    }
}

internal sealed class ApplyAutopilotCycleCommandHandler(
    IAutopilotRepository autopilots,
    IAutopilotDecisionLog decisionLog,
    IBrokerGateway broker,
    IRiskLimitsStore riskLimits,
    IOrderRepository orders,
    PortfolioRiskReader portfolio,
    IQuoteCache quotes,
    ILatestPriceSource latestPrices,
    IDispatcher dispatcher,
    ITradingEnvironment environment,
    Audit audit,
    TimeProvider clock) : ICommandHandler<ApplyAutopilotCycleCommand, AutopilotCycleSummary>
{
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    public async Task<Result<AutopilotCycleSummary>> HandleAsync(ApplyAutopilotCycleCommand command, CancellationToken cancellationToken)
    {
        var autopilot = await autopilots.GetOrCreateAsync(cancellationToken);
        if (autopilot.Mode == AutopilotMode.Off)
            return AutopilotErrors.Off();
        if (autopilot.Mode == AutopilotMode.Live)
            return AutopilotErrors.ModeNotAvailable(autopilot.Mode);

        // Checked again here, not only when the mode was chosen: the app may have been switched to the live account since.
        var paper = autopilot.Mode == AutopilotMode.Paper;
        if (paper && environment.Mode != TradingMode.Paper)
            return AutopilotErrors.NotPaperAccount();

        BrokerAccount account;
        IReadOnlyList<BrokerPosition> positions;
        try
        {
            account = await broker.GetAccountAsync(cancellationToken);
            positions = await broker.GetPositionsAsync(cancellationToken);
        }
        catch (BrokerException ex)
        {
            return TradingErrors.BrokerUnavailable(ex.Message);
        }

        var limits = riskLimits.Current;
        var rules = SizingRules.Default with { MaxPositionPercent = Math.Min(SizingRules.Default.MaxPositionPercent, limits.MaxPositionPercent) };
        var risk = await portfolio.ReadAsync(account, positions, cancellationToken);
        var openOrders = (await orders.ListUnfinishedAsync(cancellationToken)).Count;
        var heldInAccount = positions.Where(p => p.Quantity != 0).Select(p => p.Symbol).ToHashSet(StringComparer.Ordinal);
        var expected = ExchangeCalendar.PreviousSession(command.Session);
        var signals = command.Signals.Where(s => s is not null).ToDictionary(s => s.AssignmentId);
        var now = clock.GetUtcNow();
        var decisions = new List<AutopilotDecision>();
        var sent = 0;

        void Decide(AutopilotAssignment a, AutopilotAction action, decimal quantity, decimal? price, decimal? stop, string reason,
            decimal? profit = null, string? orderId = null) =>
            decisions.Add(new AutopilotDecision(now, autopilot.Mode, a.Id, a.Symbol.Value, a.Strategy.Value, action, quantity, price, stop, reason,
                orderId is not null, orderId, profit));

        string Paused(AutopilotAssignment a) => a.PausedReason is { } paused ? " Paused: " + paused : "";

        // A pair decided before the entries is not bought again in the same cycle, even if its signal still says
        // entry (e.g. after its stop was hit).
        var decided = new HashSet<int>();

        // Paper positions first take in what happened at the broker since the last cycle: fills, and stops that fired.
        foreach (var a in paper ? autopilot.Assignments.Where(a => a.IsHolding && a.PositionMode == AutopilotMode.Paper).ToList() : [])
        {
            if (await ReconcileAsync(autopilot, a, positions, cancellationToken) is not { } outcome)
                continue;

            decided.Add(a.Id);
            Decide(a, outcome.Action, outcome.Quantity, outcome.Price, null, outcome.Reason + Paused(a), outcome.Profit);
        }

        // Exits next: they free capital and position slots for today's entries.
        foreach (var a in autopilot.Assignments.Where(a => a.IsHolding && !decided.Contains(a.Id)).ToList())
        {
            decided.Add(a.Id);
            if (Unusable(a, signals, expected) is { } problem)
            {
                Decide(a, AutopilotAction.Skip, a.Quantity, null, a.StopPrice, problem + " The position is kept.");
                continue;
            }

            var signal = signals[a.Id].Signal!;
            var price = await PriceAsync(a.Symbol, signal, cancellationToken);
            var reason = price <= a.StopPrice
                ? string.Create(Invariant, $"The price {price:N2} is at or below the stop {a.StopPrice:N2}.")
                : signal.Kind switch
                {
                    SignalKind.Exit => "The strategy exited" + (signal.ExitTag is { } tag ? $" ({tag})." : "."),
                    SignalKind.Flat => "The strategy no longer holds a position.",
                    SignalKind.Short => "The strategy went short; the autopilot trades long only.",
                    _ => null
                };

            if (reason is null)
            {
                Decide(a, AutopilotAction.Hold, a.Quantity, price, a.StopPrice, string.Create(Invariant, $"The strategy still holds (since {signal.EntryTime:yyyy-MM-dd})."));
                continue;
            }

            var quantity = a.Quantity;
            if (!paper)
            {
                var profit = autopilot.Exit(a.Id, price);
                Decide(a, AutopilotAction.Sell, quantity, price, null, reason + Paused(a), profit);
                continue;
            }

            var sale = await SellAsync(a, reason, cancellationToken);
            if (sale.OrderId is null)
            {
                Decide(a, AutopilotAction.Skip, quantity, price, a.StopPrice, sale.Reason);
                continue;
            }

            sent++;
            var exitPrice = sale.FillPrice ?? price;
            var realized = autopilot.Exit(a.Id, exitPrice);
            Decide(a, AutopilotAction.Sell, quantity, exitPrice, null,
                reason + (sale.FillPrice is null ? " Profit counted at the price when the order was sent." : "") + Paused(a), realized, sale.OrderId);
        }

        // Entries by score, so the best pairs get the capital when it runs short.
        var buys = 0;
        foreach (var a in autopilot.Assignments.Where(a => a.IsActive && !a.IsHolding && !decided.Contains(a.Id)).OrderByDescending(a => a.Score ?? decimal.MinValue).ToList())
        {
            if (Unusable(a, signals, expected) is { } problem)
            {
                Decide(a, AutopilotAction.Skip, 0, null, null, problem);
                continue;
            }

            var signal = signals[a.Id].Signal!;
            if (signal.Kind != SignalKind.Entry)
            {
                Decide(a, AutopilotAction.Wait, 0, signal.LastPrice, null, signal.Kind switch
                {
                    SignalKind.Holding => string.Create(Invariant, $"The strategy entered {signal.SessionsSinceEntry} sessions ago: too late to follow."),
                    SignalKind.Exit => "The strategy has just exited.",
                    SignalKind.Short => "Short signal: the autopilot trades long only.",
                    _ => "No entry signal."
                });
                continue;
            }

            if (autopilot.Assignments.FirstOrDefault(o => o.IsHolding && o.Symbol == a.Symbol) is { } other)
            {
                Decide(a, AutopilotAction.Skip, 0, signal.LastPrice, null, $"{a.Symbol} is already held for {other.Strategy}.");
                continue;
            }

            if (heldInAccount.Contains(a.Symbol.Value))
            {
                Decide(a, AutopilotAction.Skip, 0, signal.LastPrice, null, "Already held in the account.");
                continue;
            }

            var price = await PriceAsync(a.Symbol, signal, cancellationToken);
            var moved = (price - signal.LastPrice) / signal.LastPrice * 100m;
            if (Math.Abs(moved) > TradeProposal.MaxPriceDeviationPercent)
            {
                Decide(a, AutopilotAction.Skip, 0, price, null, string.Create(Invariant,
                    $"The price moved {moved:+0.00;-0.00}% from the signal's close of {signal.LastPrice:N2}; the limit is ±{TradeProposal.MaxPriceDeviationPercent}%."));
                continue;
            }

            var capitalLeft = Math.Max(0m, account.Equity * autopilot.MaxCapitalPercent / 100m - autopilot.CapitalInUse);
            var size = PositionSizer.Size(account.Equity, Math.Min(account.BuyingPower, capitalLeft), price, signal.StopPrice, rules, limits);
            if (size.Quantity < 1)
            {
                var limitedBy = size.LimitedBy == SizeLimit.BuyingPower && capitalLeft < account.BuyingPower
                    ? string.Create(Invariant, $"the autopilot's {autopilot.MaxCapitalPercent}% of equity")
                    : size.LimitedBy.ToString();
                Decide(a, AutopilotAction.Skip, 0, price, null, $"Not even one share fits the limits (limited by {limitedBy}).");
                continue;
            }

            // The checks a real order would face, counting what this cycle already did: simulated positions are not
            // in the account, and the account was read before this cycle's buys.
            var notInAccount = paper
                ? buys
                : autopilot.Assignments.Where(o => o.IsHolding && !heldInAccount.Contains(o.Symbol.Value)).Select(o => o.Symbol).Distinct().Count();
            var context = risk.ContextFor(0, account, openOrders + sent) with
            {
                OpenPositions = risk.OpenPositions + notInAccount,
                OrdersToday = risk.OrdersToday + (paper ? sent : buys)
            };
            var check = RiskPolicy.Evaluate(new OrderIntent(a.Symbol, OrderSide.Buy, size.Quantity, price), context, limits);
            if (!check.IsAllowed)
            {
                Decide(a, AutopilotAction.Skip, size.Quantity, price, size.StopPrice, "The order would be refused: " + string.Join(" ", check.Violations));
                continue;
            }

            var why = string.Create(Invariant,
                $"Entry signal from {signal.EntryTime:yyyy-MM-dd}; risk {size.RiskAmount:N2} USD{(size.UsesFallbackStop ? " (fallback stop)." : ".")}");
            if (!paper)
            {
                autopilot.Enter(a.Id, size.Quantity, price, size.StopPrice, now);
                buys++;
                Decide(a, AutopilotAction.Buy, size.Quantity, price, size.StopPrice, why);
                continue;
            }

            var bought = await BuyAsync(a, size, cancellationToken);
            if (bought.OrderId is null)
            {
                Decide(a, AutopilotAction.Skip, size.Quantity, price, size.StopPrice, bought.Reason);
                continue;
            }

            sent++;
            buys++;
            autopilot.Enter(a.Id, size.Quantity, price, size.StopPrice, now, bought.OrderId);
            if (bought.FillPrice is { } filled)
                autopilot.ConfirmEntry(a.Id, size.Quantity, filled);
            Decide(a, AutopilotAction.Buy, size.Quantity, bought.FillPrice ?? price, size.StopPrice, why + bought.Reason, orderId: bought.OrderId);
        }

        autopilot.CompleteCycle(command.Session, now);
        await decisionLog.AppendAsync(decisions, cancellationToken);

        var summary = new AutopilotCycleSummary(
            autopilot.Mode,
            decisions.Count(d => d.Action == AutopilotAction.Buy),
            decisions.Count(d => d.Action == AutopilotAction.Sell),
            decisions.Count(d => d.Action == AutopilotAction.Hold),
            decisions.Count(d => d.Action == AutopilotAction.Wait),
            decisions.Count(d => d.Action == AutopilotAction.Skip));
        await audit.RecordAsync(AuditCategory.Autopilot, "Cycle", $"{summary} Session {command.Session:yyyy-MM-dd}; " +
            (paper ? $"{sent} orders were sent to the paper account." : "no orders were sent."));

        // Saved by UnitOfWorkBehavior.
        return summary;
    }

    private sealed record Reconciled(AutopilotAction Action, decimal Quantity, decimal? Price, string Reason, decimal? Profit = null);

    /// <summary>
    /// Takes in the fill of a pending entry and notices a position the account no longer holds (its stop fired at the
    /// broker, or it was sold by hand).
    /// </summary>
    /// <returns>A decision to record, or null when the position is as expected and the pair goes on to its signal.</returns>
    private async Task<Reconciled?> ReconcileAsync(
        Domain.Autopilot.Autopilot autopilot,
        AutopilotAssignment a,
        IReadOnlyList<BrokerPosition> positions,
        CancellationToken cancellationToken)
    {
        if (a.EntryOrderId is { } entryOrderId)
        {
            var order = await orders.GetAsync(ClientOrderId.From(entryOrderId), cancellationToken);
            if (order is null || !order.IsFinal)
            {
                return order?.FilledQuantity > 0
                    ? new Reconciled(AutopilotAction.Skip, a.Quantity, null, $"The buy order {entryOrderId} is partly filled and still open.")
                    : new Reconciled(AutopilotAction.Skip, a.Quantity, null, $"The buy order {entryOrderId} is still open.");
            }

            if (order.FilledQuantity <= 0 || order.AverageFillPrice is not { } fillPrice)
            {
                autopilot.DiscardEntry(a.Id);
                return new Reconciled(AutopilotAction.Skip, 0, null, $"The buy order {entryOrderId} ended {order.Status} without a fill; there is no position.");
            }

            autopilot.ConfirmEntry(a.Id, order.FilledQuantity, fillPrice);
        }

        var held = positions.FirstOrDefault(p => p.Symbol == a.Symbol.Value)?.Quantity ?? 0;
        if (held >= a.Quantity)
            return null;

        if (held > 0)
        {
            autopilot.ConfirmEntry(a.Id, held, a.EntryPrice!.Value);
            return null;
        }

        var quantity = a.Quantity;
        var stop = a.StopPrice!.Value;
        var profit = autopilot.Exit(a.Id, stop);
        return new Reconciled(AutopilotAction.Sell, quantity, stop,
            "No longer held on the paper account: the stop at the broker was probably hit. Profit counted at the stop.", profit);
    }

    private sealed record Sent(string? OrderId, decimal? FillPrice, string Reason);

    /// <summary>
    /// Sells the whole position at market. The broker holds the shares for the stop leg of the entry, so open sell
    /// orders on the symbol are cancelled first; if the sell is then refused, the stop is placed again.
    /// </summary>
    private async Task<Sent> SellAsync(AutopilotAssignment a, string reason, CancellationToken cancellationToken)
    {
        try
        {
            foreach (var open in await broker.ListOpenOrdersAsync(a.Symbol, cancellationToken))
            {
                if (open.Side == OrderSide.Sell)
                    await broker.CancelOrderAsync(open.BrokerOrderId, cancellationToken);
            }
        }
        catch (BrokerException ex)
        {
            return new Sent(null, null, $"Wanted to sell ({reason}) but the open sell orders could not be cancelled: {ex.Message} The position is kept.");
        }

        var placed = await dispatcher.SendAsync(new PlaceOrderCommand(a.Symbol.Value, OrderSide.Sell, OrderType.Market, a.Quantity), cancellationToken);
        if (placed.IsSuccess)
            return new Sent(placed.Value.ClientOrderId, placed.Value.Status == OrderStatus.Filled ? placed.Value.AverageFillPrice : null, "");

        var restop = await dispatcher.SendAsync(
            new PlaceOrderCommand(a.Symbol.Value, OrderSide.Sell, OrderType.Stop, a.Quantity, StopPrice: a.StopPrice, TimeInForce: TimeInForce.Gtc),
            CancellationToken.None);
        if (restop.IsFailure)
        {
            await audit.RecordAsync(AuditCategory.Risk, "Unprotected",
                $"Autopilot: {a.Quantity} {a.Symbol} could not be sold ({placed.Error.Message}) nor protected by a stop again ({restop.Error.Message}).",
                symbol: a.Symbol.Value);
        }

        return new Sent(null, null, $"Wanted to sell ({reason}) but the order was refused: {placed.Error.Message} " +
            (restop.IsSuccess
                ? string.Create(Invariant, $"The stop at {a.StopPrice:N2} was placed again.")
                : $"The stop could not be placed again ({restop.Error.Message}): the position is unprotected."));
    }

    /// <summary>
    /// Buys at market with the stop loss held by the broker. When the outcome is unknown (no answer from the broker)
    /// the order is followed as a pending entry: the next cycle reads whether it filled.
    /// </summary>
    private async Task<Sent> BuyAsync(AutopilotAssignment a, PositionSize size, CancellationToken cancellationToken)
    {
        var placed = await dispatcher.SendAsync(
            new PlaceOrderCommand(a.Symbol.Value, OrderSide.Buy, OrderType.Market, size.Quantity, StopLossPrice: size.StopPrice), cancellationToken);
        if (placed.IsSuccess)
            return new Sent(placed.Value.ClientOrderId, placed.Value.Status == OrderStatus.Filled ? placed.Value.AverageFillPrice : null, "");

        if (placed.Error.Code == "Trading.SubmitUnknown"
            && (await orders.ListUnfinishedAsync(cancellationToken))
                .Where(o => o.Symbol == a.Symbol && o.Side == OrderSide.Buy && o.Status == OrderStatus.New)
                .MaxBy(o => o.CreatedAt) is { } unknown)
        {
            return new Sent(unknown.Id.Value, null, " The broker did not answer; the order is checked at the next cycle.");
        }

        return new Sent(null, null, "The buy was refused: " + placed.Error.Message);
    }

    private static string? Unusable(AutopilotAssignment a, IReadOnlyDictionary<int, AssignmentSignal> signals, DateOnly expected)
    {
        if (!signals.TryGetValue(a.Id, out var read) || read.Signal is not { } signal)
            return "The signal could not be read" + (read?.Error is { } error ? $": {error}" : ".");

        // A missing session (e.g. an exchange holiday) also lands here: better to wait a day than act on old data.
        return signal.AsOf < expected
            ? string.Create(Invariant, $"The data ends on {signal.AsOf:yyyy-MM-dd}, before the last session ({expected:yyyy-MM-dd}).")
            : null;
    }

    /// <summary>The live price, else the latest trade, else the signal's close.</summary>
    private async Task<decimal> PriceAsync(Symbol symbol, StrategySignal signal, CancellationToken cancellationToken)
    {
        if (quotes.TryGet(symbol, out var quote) && quote.Price is { } live)
            return live;
        return await latestPrices.GetLatestPriceAsync(symbol, cancellationToken) ?? signal.LastPrice;
    }
}
