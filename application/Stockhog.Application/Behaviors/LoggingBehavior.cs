using System.Diagnostics;
using Stockhog.Application.Abstractions.Messaging;
using Stockhog.Application.Abstractions.Results;
using Microsoft.Extensions.Logging;

namespace Stockhog.Application.Behaviors;

/// <summary>
/// Outermost behavior: logs every request with its outcome and duration. Successful queries log at Debug level:
/// pages poll them (e.g. the account every few seconds), which would drown the commands in the log.
/// </summary>
internal sealed class LoggingBehavior<TRequest, TResponse>(ILogger<LoggingBehavior<TRequest, TResponse>> logger)
    : IPipelineBehavior<TRequest, TResponse>
    where TRequest : IRequest<TResponse>
{
    public async Task<Result<TResponse>> HandleAsync(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken)
    {
        var requestName = typeof(TRequest).Name;
        var started = Stopwatch.GetTimestamp();

        try
        {
            var result = await next().ConfigureAwait(false);
            var elapsedMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;

            if (result.IsSuccess)
                logger.Log(request is IQuery<TResponse> ? LogLevel.Debug : LogLevel.Information, "{Request} succeeded in {ElapsedMs:0} ms", requestName, elapsedMs);
            else
                logger.LogWarning("{Request} failed in {ElapsedMs:0} ms: {ErrorCode} {ErrorMessage}",
                    requestName, elapsedMs, result.Error.Code, result.Error.Message);

            return result;
        }
        catch (OperationCanceledException)
        {
            logger.LogInformation("{Request} cancelled after {ElapsedMs:0} ms", requestName, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "{Request} threw after {ElapsedMs:0} ms", requestName, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            throw;
        }
    }
}
