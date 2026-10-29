using Stockhog.Application.Abstractions.Results;

namespace Stockhog.Application.Abstractions.Messaging;

/// <summary>
/// A message handled by exactly one handler, which returns <see cref="Result{T}"/> of <typeparamref name="TResponse"/>.
/// </summary>
public interface IRequest<TResponse>;

/// <summary>Changes state.</summary>
public interface ICommand<TResponse> : IRequest<TResponse>;

/// <summary>Reads state without changing it.</summary>
public interface IQuery<TResponse> : IRequest<TResponse>;

public interface IRequestHandler<in TRequest, TResponse>
    where TRequest : IRequest<TResponse>
{
    Task<Result<TResponse>> HandleAsync(TRequest request, CancellationToken cancellationToken);
}

public interface ICommandHandler<in TCommand, TResponse> : IRequestHandler<TCommand, TResponse>
    where TCommand : ICommand<TResponse>;

public interface IQueryHandler<in TQuery, TResponse> : IRequestHandler<TQuery, TResponse>
    where TQuery : IQuery<TResponse>;

public delegate Task<Result<TResponse>> RequestHandlerDelegate<TResponse>();

/// <summary>
/// Cross-cutting step around every handler (logging, validation, caching). Behaviors run in registration order, outermost first.
/// </summary>
public interface IPipelineBehavior<in TRequest, TResponse>
    where TRequest : IRequest<TResponse>
{
    Task<Result<TResponse>> HandleAsync(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken);
}

public interface IDispatcher
{
    /// <summary>
    /// Runs the request through the pipeline in a new DI scope.
    /// </summary>
    Task<Result<TResponse>> SendAsync<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default);
}

/// <summary>
/// Successful results of a query implementing this interface are cached by key. Failures are never cached.
/// </summary>
public interface ICacheableQuery
{
    string CacheKey { get; }

    /// <summary>Tags let commands invalidate groups of entries, see <c>HybridCache.RemoveByTagAsync</c>.</summary>
    IReadOnlyCollection<string> CacheTags { get; }

    TimeSpan CacheDuration { get; }
}
