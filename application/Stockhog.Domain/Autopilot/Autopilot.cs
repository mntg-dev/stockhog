using Stockhog.Domain.Backtesting;
using Stockhog.Domain.Common;
using Stockhog.Domain.MarketData;

namespace Stockhog.Domain.Autopilot;

/// <summary>How the autopilot acts on its decisions.</summary>
public enum AutopilotMode
{
    /// <summary>Does nothing.</summary>
    Off,

    /// <summary>Decides as it would and records it, tracking simulated positions; sends no order.</summary>
    Shadow,

    /// <summary>Places the orders on the paper account, each buy with a stop loss held by the broker.</summary>
    Paper,

    /// <summary>Places the orders on the live account. Not available yet.</summary>
    Live
}

/// <summary>
/// One strategy the autopilot follows on one symbol, with the position it holds for it. In shadow mode the position
/// is simulated at the prices the decisions were made at, so the user can see what following it would have done; in
/// paper mode it is a real position on the paper account. Changed only through its <see cref="Autopilot"/>.
/// </summary>
public sealed class AutopilotAssignment : Entity<int>
{
    internal AutopilotAssignment(int id, Symbol symbol, StrategyName strategy, decimal? score, DateTimeOffset addedAt) : base(id)
    {
        Symbol = symbol;
        Strategy = strategy;
        Score = score;
        AddedAt = addedAt;
        Enabled = true;
    }

#pragma warning disable CS8618 // Persistence materializes assignments through this constructor and then sets every property.
    private AutopilotAssignment() : base(0)
    {
    }
#pragma warning restore CS8618

    public Symbol Symbol { get; }
    public StrategyName Strategy { get; }

    /// <summary>The pair's scan score when it was added.</summary>
    public decimal? Score { get; }

    public DateTimeOffset AddedAt { get; }
    public bool Enabled { get; private set; }

    /// <summary>Why the autopilot stopped following it, e.g. after too many losses in a row; null while active.</summary>
    public string? PausedReason { get; private set; }

    /// <summary>Shares held for this pair (simulated in shadow mode); 0 when flat.</summary>
    public decimal Quantity { get; private set; }

    public decimal? EntryPrice { get; private set; }
    public DateTimeOffset? EntryAt { get; private set; }
    public decimal? StopPrice { get; private set; }

    /// <summary>Whether the position is simulated (shadow) or held on the account (paper); null when flat.</summary>
    public AutopilotMode? PositionMode { get; private set; }

    /// <summary>
    /// The buy order of a paper position until its fill has been read; the quantity and entry price are the ones
    /// ordered until then.
    /// </summary>
    public string? EntryOrderId { get; private set; }

    /// <summary>Profit of the closed round trips, in USD.</summary>
    public decimal RealizedProfit { get; private set; }

    public int ClosedTrades { get; private set; }
    public int WinningTrades { get; private set; }
    public int ConsecutiveLosses { get; private set; }

    public bool IsActive => Enabled && PausedReason is null;

    public bool IsHolding => Quantity > 0;

    public bool IsEntryPending => EntryOrderId is not null;

    internal void Open(decimal quantity, decimal price, decimal stopPrice, DateTimeOffset at, AutopilotMode mode, string? entryOrderId)
    {
        if (IsHolding)
            throw new DomainException($"{Symbol}/{Strategy} already holds {Quantity} shares.");
        if (quantity <= 0 || price <= 0 || stopPrice <= 0 || stopPrice >= price)
            throw new DomainException("An entry needs a positive quantity and price and a stop below the price.");

        Quantity = quantity;
        EntryPrice = price;
        EntryAt = at;
        StopPrice = stopPrice;
        PositionMode = mode;
        EntryOrderId = entryOrderId;
    }

    internal void ConfirmEntry(decimal quantity, decimal price)
    {
        if (!IsHolding)
            throw new DomainException($"{Symbol}/{Strategy} holds nothing to confirm.");
        if (quantity <= 0 || price <= 0)
            throw new DomainException("A filled entry needs a positive quantity and price.");

        Quantity = quantity;
        EntryPrice = price;
        EntryOrderId = null;
    }

    /// <summary>Forgets the position without counting a trade: an entry that never filled, or a simulated position.</summary>
    internal void Discard() => Clear();

    /// <returns>The round trip's profit in USD.</returns>
    internal decimal Close(decimal price)
    {
        if (!IsHolding)
            throw new DomainException($"{Symbol}/{Strategy} holds nothing to close.");
        if (price <= 0)
            throw new DomainException("The exit price must be positive.");

        var profit = Math.Round(Quantity * (price - EntryPrice!.Value), 2);
        RealizedProfit += profit;
        ClosedTrades++;
        if (profit > 0)
        {
            WinningTrades++;
            ConsecutiveLosses = 0;
        }
        else
        {
            ConsecutiveLosses++;
        }

        Clear();
        return profit;
    }

    private void Clear()
    {
        Quantity = 0;
        EntryPrice = null;
        EntryAt = null;
        StopPrice = null;
        PositionMode = null;
        EntryOrderId = null;
    }

    internal void Pause(string reason) => PausedReason = reason;

    internal void Resume()
    {
        PausedReason = null;
        ConsecutiveLosses = 0;
    }

    internal void SetEnabled(bool enabled) => Enabled = enabled;
}

/// <summary>
/// The automatic trader: which pairs it follows, in which mode, and the limits it keeps to on top of the account's
/// risk limits. One per app. Every decision it takes is recorded as an <see cref="AutopilotDecision"/>.
/// </summary>
public sealed class Autopilot : AggregateRoot<string>
{
    public const string SingletonId = "autopilot";
    public const int MaxAssignments = 50;

    private readonly List<AutopilotAssignment> _assignments = [];

    private Autopilot() : base(SingletonId)
    {
        Mode = AutopilotMode.Off;
        MaxConsecutiveLosses = 3;
        MaxCapitalPercent = 50m;
    }

    public AutopilotMode Mode { get; private set; }

    /// <summary>A pair is paused after this many losing round trips in a row.</summary>
    public int MaxConsecutiveLosses { get; private set; }

    /// <summary>Most of the equity the autopilot's positions may take together, e.g. 50 for 50%.</summary>
    public decimal MaxCapitalPercent { get; private set; }

    /// <summary>The session the last cycle ran for: one cycle per session.</summary>
    public DateOnly? LastCycleSession { get; private set; }

    public DateTimeOffset? LastCycleAt { get; private set; }

    /// <summary>The id of the last pair added. Ids are never reused, so the decision log stays unambiguous.</summary>
    public int LastAssignmentId { get; private set; }

    public IReadOnlyList<AutopilotAssignment> Assignments => _assignments;

    public static Autopilot Create() => new();

    /// <summary>
    /// Live is not available yet. Switching to paper forgets the simulated positions, as they are not on the account.
    /// While paper positions are open the autopilot can only go back to paper or off (their stops stay at the broker),
    /// never to shadow, which would take them for simulated ones.
    /// </summary>
    /// <returns>How many simulated positions were forgotten.</returns>
    public int SetMode(AutopilotMode mode)
    {
        if (mode == AutopilotMode.Live)
            throw new DomainException("Live mode is not available yet; run the autopilot on the paper account first.");
        if (mode == AutopilotMode.Shadow && PaperPositions > 0)
            throw new DomainException($"{PaperPositions} positions are open on the paper account; switch the autopilot off instead.");

        var discarded = 0;
        if (mode == AutopilotMode.Paper)
        {
            foreach (var simulated in _assignments.Where(a => a.PositionMode == AutopilotMode.Shadow))
            {
                simulated.Discard();
                discarded++;
            }
        }

        Mode = mode;
        return discarded;
    }

    /// <summary>
    /// Called at startup: the autopilot never places orders after a restart until the user switches it on again.
    /// Open positions keep their stops at the broker.
    /// </summary>
    /// <returns>Whether it was trading and is now off.</returns>
    public bool SuspendAfterRestart()
    {
        if (Mode is not (AutopilotMode.Paper or AutopilotMode.Live))
            return false;
        Mode = AutopilotMode.Off;
        return true;
    }

    public int PaperPositions => _assignments.Count(a => a.IsHolding && a.PositionMode == AutopilotMode.Paper);

    public void Configure(int maxConsecutiveLosses, decimal maxCapitalPercent)
    {
        if (maxConsecutiveLosses is < 1 or > 20)
            throw new DomainException("Pause after between 1 and 20 losses in a row.");
        if (maxCapitalPercent is <= 0 or > 100)
            throw new DomainException("The autopilot may use above 0% and at most 100% of equity.");

        MaxConsecutiveLosses = maxConsecutiveLosses;
        MaxCapitalPercent = maxCapitalPercent;
    }

    /// <returns>The new assignment, or <c>null</c> if the pair is already followed.</returns>
    public AutopilotAssignment? Add(Symbol symbol, StrategyName strategy, decimal? score, DateTimeOffset at)
    {
        if (_assignments.Any(a => a.Symbol == symbol && a.Strategy == strategy))
            return null;
        if (_assignments.Count >= MaxAssignments)
            throw new DomainException($"The autopilot follows at most {MaxAssignments} pairs.");

        var assignment = new AutopilotAssignment(++LastAssignmentId, symbol, strategy, score, at);
        _assignments.Add(assignment);
        return assignment;
    }

    /// <summary>A pair still holding a position cannot be removed: disable it, or wait until it exits.</summary>
    public void Remove(int assignmentId)
    {
        var assignment = Assignment(assignmentId);
        if (assignment.IsHolding)
            throw new DomainException($"{assignment.Symbol}/{assignment.Strategy} still holds {assignment.Quantity} shares.");
        _assignments.Remove(assignment);
    }

    public void SetEnabled(int assignmentId, bool enabled) => Assignment(assignmentId).SetEnabled(enabled);

    public void Resume(int assignmentId) => Assignment(assignmentId).Resume();

    /// <summary>Capital the open positions take at their entry prices.</summary>
    public decimal CapitalInUse => _assignments.Where(a => a.IsHolding).Sum(a => a.Quantity * a.EntryPrice!.Value);

    /// <summary>Opens a position: a real one in paper mode, with the buy order it waits for, else a simulated one.</summary>
    public void Enter(int assignmentId, decimal quantity, decimal price, decimal stopPrice, DateTimeOffset at, string? entryOrderId = null)
    {
        var mode = Mode == AutopilotMode.Paper ? AutopilotMode.Paper : AutopilotMode.Shadow;
        if (mode == AutopilotMode.Paper && entryOrderId is null)
            throw new DomainException("A paper position needs the order that opened it.");
        Assignment(assignmentId).Open(quantity, price, stopPrice, at, mode, entryOrderId);
    }

    /// <summary>The entry order filled: the position becomes what was actually bought.</summary>
    public void ConfirmEntry(int assignmentId, decimal quantity, decimal price) => Assignment(assignmentId).ConfirmEntry(quantity, price);

    /// <summary>The entry order ended without a fill: there is no position, and no trade is counted.</summary>
    public void DiscardEntry(int assignmentId) => Assignment(assignmentId).Discard();

    /// <summary>Closes the pair's position; pauses the pair once it has lost too often in a row.</summary>
    /// <returns>The round trip's profit in USD.</returns>
    public decimal Exit(int assignmentId, decimal price)
    {
        var assignment = Assignment(assignmentId);
        var profit = assignment.Close(price);
        if (assignment.ConsecutiveLosses >= MaxConsecutiveLosses)
            assignment.Pause($"{assignment.ConsecutiveLosses} losing trades in a row.");
        return profit;
    }

    public void CompleteCycle(DateOnly session, DateTimeOffset at)
    {
        LastCycleSession = session;
        LastCycleAt = at;
    }

    private AutopilotAssignment Assignment(int id) =>
        _assignments.FirstOrDefault(a => a.Id == id) ?? throw new DomainException($"The autopilot has no assignment {id}.");
}

public enum AutopilotAction
{
    Buy,
    Sell,

    /// <summary>Keeps the position it holds.</summary>
    Hold,

    /// <summary>Flat and nothing to do: no fresh entry.</summary>
    Wait,

    /// <summary>Wanted to act but could not: stale data, limits, no signal.</summary>
    Skip
}

/// <summary>One decision of one cycle, kept as an append-only log.</summary>
/// <param name="Executed">Whether an order was sent; always false in shadow mode.</param>
public sealed record AutopilotDecision(
    DateTimeOffset Time,
    AutopilotMode Mode,
    int AssignmentId,
    string Symbol,
    string StrategyName,
    AutopilotAction Action,
    decimal Quantity,
    decimal? Price,
    decimal? StopPrice,
    string Reason,
    bool Executed,
    string? OrderId = null,
    decimal? Profit = null);
