using Stockhog.Application.Abstractions.Messaging;
using Stockhog.Application.Abstractions.Results;
using Stockhog.Domain.Autopilot;

namespace Stockhog.Application.Autopilot;

/// <summary>The autopilot's mode, limits and the pairs it follows, for the autopilot page.</summary>
public sealed record GetAutopilotQuery : IQuery<AutopilotView>;

public sealed record AutopilotView(
    AutopilotMode Mode,
    int MaxConsecutiveLosses,
    decimal MaxCapitalPercent,
    DateOnly? LastCycleSession,
    DateTimeOffset? LastCycleAt,
    decimal CapitalInUse,
    IReadOnlyList<AutopilotAssignmentView> Assignments);

/// <param name="WinRatePercent">Winning share of the closed round trips; null before the first one.</param>
public sealed record AutopilotAssignmentView(
    int Id,
    string Symbol,
    string StrategyName,
    decimal? Score,
    DateTimeOffset AddedAt,
    bool Enabled,
    string? PausedReason,
    decimal Quantity,
    decimal? EntryPrice,
    DateTimeOffset? EntryAt,
    decimal? StopPrice,
    decimal RealizedProfit,
    int ClosedTrades,
    int WinningTrades,
    int ConsecutiveLosses)
{
    public bool IsHolding => Quantity > 0;

    public bool IsPaused => PausedReason is not null;

    public decimal? WinRatePercent => ClosedTrades == 0 ? null : Math.Round(WinningTrades * 100m / ClosedTrades, 1);
}

/// <summary>The latest decisions, newest first.</summary>
public sealed record GetAutopilotDecisionsQuery(int Take = 200) : IQuery<IReadOnlyList<AutopilotDecision>>;

internal sealed class GetAutopilotQueryHandler(IAutopilotRepository autopilots) : IQueryHandler<GetAutopilotQuery, AutopilotView>
{
    public async Task<Result<AutopilotView>> HandleAsync(GetAutopilotQuery query, CancellationToken cancellationToken)
    {
        var a = await autopilots.GetOrCreateAsync(cancellationToken);
        return new AutopilotView(
            a.Mode,
            a.MaxConsecutiveLosses,
            a.MaxCapitalPercent,
            a.LastCycleSession,
            a.LastCycleAt,
            a.CapitalInUse,
            a.Assignments
                .OrderByDescending(x => x.IsHolding).ThenByDescending(x => x.Score ?? decimal.MinValue)
                .Select(x => new AutopilotAssignmentView(
                    x.Id, x.Symbol.Value, x.Strategy.Value, x.Score, x.AddedAt, x.Enabled, x.PausedReason, x.Quantity, x.EntryPrice, x.EntryAt,
                    x.StopPrice, x.RealizedProfit, x.ClosedTrades, x.WinningTrades, x.ConsecutiveLosses))
                .ToList());
    }
}

internal sealed class GetAutopilotDecisionsQueryHandler(IAutopilotDecisionLog log) : IQueryHandler<GetAutopilotDecisionsQuery, IReadOnlyList<AutopilotDecision>>
{
    public async Task<Result<IReadOnlyList<AutopilotDecision>>> HandleAsync(GetAutopilotDecisionsQuery query, CancellationToken cancellationToken) =>
        Result<IReadOnlyList<AutopilotDecision>>.Success(await log.ListAsync(Math.Clamp(query.Take, 1, 1_000), cancellationToken));
}
