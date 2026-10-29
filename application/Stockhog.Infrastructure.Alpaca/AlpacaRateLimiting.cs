using System.Threading.RateLimiting;
using Microsoft.Extensions.Options;

namespace Stockhog.Infrastructure.Alpaca;

/// <summary>
/// One token bucket shared by every Alpaca request in the process, sized to the plan's requests-per-minute limit.
/// Requests beyond the limit wait in a queue instead of failing with HTTP 429.
/// </summary>
internal sealed class AlpacaRateLimiter : IDisposable
{
    // Tokens are replenished in small steps so throughput is smooth rather than a burst once a minute.
    private static readonly TimeSpan ReplenishmentPeriod = TimeSpan.FromSeconds(3);

    public AlpacaRateLimiter(IOptions<AlpacaOptions> options)
    {
        var perMinute = options.Value.RequestsPerMinute;
        Limiter = new TokenBucketRateLimiter(new TokenBucketRateLimiterOptions
        {
            TokenLimit = Math.Max(1, perMinute / 4),
            TokensPerPeriod = Math.Max(1, perMinute / 20),
            ReplenishmentPeriod = ReplenishmentPeriod,
            QueueLimit = 10_000,
            QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
            AutoReplenishment = true
        });
    }

    internal AlpacaRateLimiter(RateLimiter limiter)
    {
        Limiter = limiter;
    }

    public RateLimiter Limiter { get; }

    public void Dispose() => Limiter.Dispose();
}

/// <summary>
/// Sits inside the resilience pipeline, so every attempt, including retries, waits for a token.
/// </summary>
internal sealed class AlpacaRateLimitingHandler(AlpacaRateLimiter rateLimiter) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        using var lease = await rateLimiter.Limiter.AcquireAsync(1, cancellationToken).ConfigureAwait(false);
        if (!lease.IsAcquired)
            throw new HttpRequestException("Too many Alpaca requests are waiting; try again later.");

        return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
    }
}
