using Stockhog.Application.Abstractions.Events;
using Stockhog.Application.Abstractions.Messaging;
using Stockhog.Application.Abstractions.Results;
using Stockhog.Application.Common;
using Stockhog.Domain.Backtesting;
using Stockhog.Domain.MarketData;
using FluentValidation;
using Microsoft.Extensions.Caching.Hybrid;

namespace Stockhog.Application.Backtesting.GetLatestBacktestReport;

/// <summary>
/// Most recent report for a symbol and strategy. Cached until a new run of the same pair completes
/// (see <see cref="InvalidateLatestReportWhenRunCompleted"/>).
/// </summary>
public sealed record GetLatestBacktestReportQuery(string Symbol, string StrategyName) : IQuery<BacktestReport>, ICacheableQuery
{
    private string NormalizedSymbol => Symbol.Trim().ToUpperInvariant();

    public string CacheKey => BacktestCacheKeys.LatestReport(NormalizedSymbol, StrategyName);

    public IReadOnlyCollection<string> CacheTags => [BacktestCacheKeys.Tag(NormalizedSymbol, StrategyName)];

    public TimeSpan CacheDuration => TimeSpan.FromMinutes(30);
}

internal sealed class GetLatestBacktestReportQueryValidator : AbstractValidator<GetLatestBacktestReportQuery>
{
    public GetLatestBacktestReportQueryValidator()
    {
        RuleFor(q => q.Symbol).MustBeSymbol();
        RuleFor(q => q.StrategyName).MustBeStrategyName();
    }
}

internal sealed class GetLatestBacktestReportQueryHandler(IBacktestResultReader reader)
    : IQueryHandler<GetLatestBacktestReportQuery, BacktestReport>
{
    public async Task<Result<BacktestReport>> HandleAsync(GetLatestBacktestReportQuery query, CancellationToken cancellationToken)
    {
        var symbol = Symbol.Create(query.Symbol);
        var strategy = StrategyName.Create(query.StrategyName);

        var report = await reader.ReadLatestAsync(symbol, strategy, cancellationToken);
        return report is null ? BacktestErrors.NoResults(symbol.Value, strategy.Value) : report;
    }
}

internal sealed class InvalidateLatestReportWhenRunCompleted(HybridCache cache) : IDomainEventHandler<BacktestRunCompleted>
{
    public async Task HandleAsync(BacktestRunCompleted domainEvent, CancellationToken cancellationToken) =>
        await cache.RemoveByTagAsync(BacktestCacheKeys.Tag(domainEvent.Symbol.Value, domainEvent.Strategy.Value), cancellationToken);
}
