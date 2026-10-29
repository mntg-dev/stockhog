using Stockhog.Application.Abstractions.Messaging;
using Stockhog.Application.Abstractions.Results;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Logging;

namespace Stockhog.Application.Behaviors;

/// <summary>
/// Caches successful results of <see cref="ICacheableQuery"/> requests in <see cref="HybridCache"/>.
/// HybridCache runs the handler only once when several callers ask for the same missing key at the same time,
/// and supports invalidation by tag.
/// </summary>
internal sealed class CachingBehavior<TRequest, TResponse>(HybridCache cache, ILogger<CachingBehavior<TRequest, TResponse>> logger)
    : IPipelineBehavior<TRequest, TResponse>
    where TRequest : IRequest<TResponse>
{
    public async Task<Result<TResponse>> HandleAsync(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken)
    {
        if (request is not ICacheableQuery cacheable)
            return await next().ConfigureAwait(false);

        var options = new HybridCacheEntryOptions
        {
            Expiration = cacheable.CacheDuration,
            LocalCacheExpiration = cacheable.CacheDuration
        };
        var handlerRan = false;

        try
        {
            var value = await cache.GetOrCreateAsync(
                cacheable.CacheKey,
                async _ =>
                {
                    handlerRan = true;
                    var result = await next().ConfigureAwait(false);
                    // Throwing is the only way to keep HybridCache from storing the entry.
                    return result.IsSuccess ? result.Value : throw new UncachedFailureException(result.Error);
                },
                options,
                cacheable.CacheTags,
                cancellationToken).ConfigureAwait(false);

            logger.LogDebug("Cache {Outcome} for {CacheKey}", handlerRan ? "miss" : "hit", cacheable.CacheKey);
            return value;
        }
        catch (UncachedFailureException ex)
        {
            return ex.Error;
        }
    }

    private sealed class UncachedFailureException(Error error) : Exception(error.Message)
    {
        public Error Error { get; } = error;
    }
}
