using Microsoft.Extensions.Http.Resilience;

namespace Stockhog.Infrastructure.Http;

/// <summary>
/// HTTP settings shared by the adapters' typed clients.
/// </summary>
public static class HttpClientDefaults
{
    /// <summary>Pooled connections are recycled so long-lived typed clients still pick up DNS changes.</summary>
    public static HttpMessageHandler CreatePrimaryHandler() =>
        new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5) };

    /// <summary>
    /// Timeouts, retries with exponential backoff (honoring Retry-After) and a circuit breaker.
    /// </summary>
    public static void ConfigureResilience(HttpStandardResilienceOptions options)
    {
        options.Retry.MaxRetryAttempts = 4;
        // An attempt may include waiting for a rate-limit token, so allow for some queueing.
        options.AttemptTimeout.Timeout = TimeSpan.FromSeconds(30);
        options.CircuitBreaker.SamplingDuration = TimeSpan.FromMinutes(1);
        options.TotalRequestTimeout.Timeout = TimeSpan.FromMinutes(3);
    }
}
