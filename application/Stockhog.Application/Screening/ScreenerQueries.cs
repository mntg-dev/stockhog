using Stockhog.Application.Abstractions.Messaging;
using Stockhog.Application.Abstractions.Results;
using Stockhog.Application.Screening.RunScreener;
using FluentValidation;

namespace Stockhog.Application.Screening;

/// <summary>The newest screener runs, newest first, without their symbols.</summary>
public sealed record GetScreenerRunsQuery(int Take = 20) : IQuery<IReadOnlyList<ScreenerRunSummary>>;

/// <summary>A screener run with its ranked symbols; <c>null</c> for the newest run. Cached: runs never change.</summary>
public sealed record GetScreenerResultQuery(Guid? Id = null) : IQuery<ScreenerResult>, ICacheableQuery
{
    public string CacheKey => $"screener:{Id?.ToString() ?? "latest"}";

    public IReadOnlyCollection<string> CacheTags { get; } = [ScreenerKeys.CacheTag];

    public TimeSpan CacheDuration => TimeSpan.FromHours(1);
}

internal sealed class GetScreenerRunsQueryValidator : AbstractValidator<GetScreenerRunsQuery>
{
    public GetScreenerRunsQueryValidator() => RuleFor(q => q.Take).InclusiveBetween(1, 100);
}

internal sealed class GetScreenerRunsQueryHandler(IScreenerResultStore store) : IQueryHandler<GetScreenerRunsQuery, IReadOnlyList<ScreenerRunSummary>>
{
    public async Task<Result<IReadOnlyList<ScreenerRunSummary>>> HandleAsync(GetScreenerRunsQuery query, CancellationToken cancellationToken) =>
        Result<IReadOnlyList<ScreenerRunSummary>>.Success(await store.ListRunsAsync(query.Take, cancellationToken).ConfigureAwait(false));
}

internal sealed class GetScreenerResultQueryHandler(IScreenerResultStore store) : IQueryHandler<GetScreenerResultQuery, ScreenerResult>
{
    public async Task<Result<ScreenerResult>> HandleAsync(GetScreenerResultQuery query, CancellationToken cancellationToken) =>
        await store.GetAsync(query.Id, cancellationToken).ConfigureAwait(false) is { } result
            ? result
            : ScreenerErrors.NotFound(query.Id);
}
