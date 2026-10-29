using Stockhog.Application.Abstractions.Messaging;
using Stockhog.Application.Abstractions.Results;
using Stockhog.Application.Common;
using Stockhog.Domain.MarketData;
using FluentValidation;

namespace Stockhog.Application.Backtesting.GetBacktestHistory;

/// <summary>
/// Recent runs, newest first. Not cached: the list changes with every run and reading it is cheap.
/// </summary>
/// <param name="Symbol">Filter by symbol, or <c>null</c> for all symbols.</param>
/// <param name="IncludeTrials">Also list the runs of optimizations, which are otherwise shown with their optimization.</param>
public sealed record GetBacktestHistoryQuery(string? Symbol = null, int Take = 100, bool IncludeTrials = false) : IQuery<IReadOnlyList<BacktestRunSummary>>;

internal sealed class GetBacktestHistoryQueryValidator : AbstractValidator<GetBacktestHistoryQuery>
{
    public GetBacktestHistoryQueryValidator()
    {
        RuleFor(q => q.Symbol!).MustBeSymbol().When(q => !string.IsNullOrWhiteSpace(q.Symbol));
        RuleFor(q => q.Take).InclusiveBetween(1, 1000);
    }
}

internal sealed class GetBacktestHistoryQueryHandler(IBacktestHistoryReader reader)
    : IQueryHandler<GetBacktestHistoryQuery, IReadOnlyList<BacktestRunSummary>>
{
    public async Task<Result<IReadOnlyList<BacktestRunSummary>>> HandleAsync(GetBacktestHistoryQuery query, CancellationToken cancellationToken)
    {
        var symbol = string.IsNullOrWhiteSpace(query.Symbol) ? null : Symbol.Create(query.Symbol);
        var runs = await reader.ListAsync(symbol, query.Take, query.IncludeTrials, cancellationToken);
        return Result<IReadOnlyList<BacktestRunSummary>>.Success(runs);
    }
}
