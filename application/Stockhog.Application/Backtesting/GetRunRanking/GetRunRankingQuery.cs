using Stockhog.Application.Abstractions.Messaging;
using Stockhog.Application.Abstractions.Results;

namespace Stockhog.Application.Backtesting.GetRunRanking;

/// <summary>
/// Completed runs with their metrics, best net profit first, for comparing many symbols or strategies.
/// </summary>
/// <param name="StrategyName">Only runs of this strategy, or every strategy when <c>null</c>.</param>
/// <param name="LatestPerSymbol">Keep only the newest run of each symbol and strategy, so repeated runs of one
/// symbol do not fill the table.</param>
/// <param name="Symbols">Only these symbols, or every symbol when <c>null</c> or empty.</param>
public sealed record GetRunRankingQuery(
    string? StrategyName = null,
    bool LatestPerSymbol = true,
    IReadOnlyCollection<string>? Symbols = null) : IQuery<IReadOnlyList<RankedRun>>;

internal sealed class GetRunRankingQueryHandler(IBacktestHistoryReader reader) : IQueryHandler<GetRunRankingQuery, IReadOnlyList<RankedRun>>
{
    public async Task<Result<IReadOnlyList<RankedRun>>> HandleAsync(GetRunRankingQuery query, CancellationToken cancellationToken)
    {
        IEnumerable<RankedRun> runs = await reader.ListRankedAsync(cancellationToken);

        if (!string.IsNullOrWhiteSpace(query.StrategyName))
            runs = runs.Where(r => r.StrategyName == query.StrategyName);

        if (query.Symbols is { Count: > 0 } symbols)
        {
            var wanted = symbols.Select(s => s.Trim().ToUpperInvariant()).ToHashSet();
            runs = runs.Where(r => wanted.Contains(r.Symbol));
        }

        // The reader returns newest first, so the first of each group is the latest run.
        if (query.LatestPerSymbol)
            runs = runs.GroupBy(r => (r.Symbol, r.StrategyName)).Select(g => g.First());

        return runs.OrderByDescending(r => r.NetProfitPercent).ThenBy(r => r.Symbol, StringComparer.Ordinal).ToList();
    }
}
