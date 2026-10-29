using Stockhog.Application.Abstractions.Messaging;
using Stockhog.Application.Abstractions.Results;

namespace Stockhog.Application.Instruments;

/// <summary>
/// The whole instrument list, ordered by symbol. Cached until the next refresh: symbol pickers search it in memory.
/// </summary>
public sealed record GetInstrumentsQuery : IQuery<IReadOnlyList<InstrumentInfo>>, ICacheableQuery
{
    public string CacheKey => "instruments:all";

    public IReadOnlyCollection<string> CacheTags { get; } = [InstrumentCacheKeys.Tag];

    public TimeSpan CacheDuration => TimeSpan.FromDays(1);
}

/// <summary>How many instruments are stored and when they were downloaded.</summary>
public sealed record GetInstrumentCatalogStatusQuery : IQuery<InstrumentCatalogStatus>;

internal sealed class GetInstrumentsQueryHandler(IInstrumentStore store) : IQueryHandler<GetInstrumentsQuery, IReadOnlyList<InstrumentInfo>>
{
    public async Task<Result<IReadOnlyList<InstrumentInfo>>> HandleAsync(GetInstrumentsQuery query, CancellationToken cancellationToken) =>
        Result<IReadOnlyList<InstrumentInfo>>.Success(await store.ListAsync(cancellationToken).ConfigureAwait(false));
}

internal sealed class GetInstrumentCatalogStatusQueryHandler(IInstrumentStore store) : IQueryHandler<GetInstrumentCatalogStatusQuery, InstrumentCatalogStatus>
{
    public async Task<Result<InstrumentCatalogStatus>> HandleAsync(GetInstrumentCatalogStatusQuery query, CancellationToken cancellationToken) =>
        await store.GetStatusAsync(cancellationToken).ConfigureAwait(false);
}
