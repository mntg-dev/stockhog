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
    Audit audit,
    TimeProvider clock) : ICommandHandler<ApplyAutopilotCycleCommand, AutopilotCycleSummary>
{
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    public async Task<Result<AutopilotCycleSummary>> HandleAsync(ApplyAutopilotCycleCommand command, CancellationToken cancellationToken)
    {
        var autopilot = await autopilots.GetOrCreateAsync(cancellationToken);
        if (autopilot.Mode == AutopilotMode.Off)
            return AutopilotErrors.Off();
        if (autopilot.Mode != AutopilotMode.Shadow)
            return AutopilotErrors.ModeNotAvailable(autopilot.Mode);

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

        AutopilotDecision Decide(AutopilotAssignment a, AutopilotAction action, decimal quantity, decimal? price, decimal? stop, string reason, decimal? profit = null)
        {
            var decision = new AutopilotDecision(now, autopilot.Mode, a.Id, a.Symbol.Value, a.Strategy.Value, action, quantity, price, stop, reason, false, Profit: profit);
            decisions.Add(decision);
            return decision;
        }

        // Exits first: they free capital and position slots for today's entries. A pair decided here is not bought
        // again in the same cycle, even if its signal still says entry (e.g. after its stop was hit).
        var decided = new HashSet<int>();
        foreach (var a in autopilot.Assignments.Where(a => a.IsHolding).ToList())
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
            var profit = autopilot.Exit(a.Id, price);
            if (a.PausedReason is { } paused)
                reason += " Paused: " + paused;
            Decide(a, AutopilotAction.Sell, quantity, price, null, reason, profit);
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

            // The checks a real order would face. Shadow positions are not in the account, so they are counted here.
            var shadowHeld = autopilot.Assignments.Where(o => o.IsHolding && !heldInAccount.Contains(o.Symbol.Value)).Select(o => o.Symbol).Distinct().Count();
            var context = risk.ContextFor(0, account, openOrders) with
            {
                OpenPositions = risk.OpenPositions + shadowHeld,
                OrdersToday = risk.OrdersToday + buys
            };
            var check = RiskPolicy.Evaluate(new OrderIntent(a.Symbol, OrderSide.Buy, size.Quantity, price), context, limits);
            if (!check.IsAllowed)
            {
                Decide(a, AutopilotAction.Skip, size.Quantity, price, size.StopPrice, "The order would be refused: " + string.Join(" ", check.Violations));
                continue;
            }

            autopilot.Enter(a.Id, size.Quantity, price, size.StopPrice, now);
            buys++;
            Decide(a, AutopilotAction.Buy, size.Quantity, price, size.StopPrice, string.Create(Invariant,
                $"Entry signal from {signal.EntryTime:yyyy-MM-dd}; risk {size.RiskAmount:N2} USD{(size.UsesFallbackStop ? " (fallback stop)." : ".")}"));
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
        await audit.RecordAsync(AuditCategory.Autopilot, "Cycle", $"{summary} Session {command.Session:yyyy-MM-dd}; no orders were sent.");

        // Saved by UnitOfWorkBehavior.
        return summary;
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
