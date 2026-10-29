using System.Collections.Concurrent;
using Stockhog.Application.Abstractions.Messaging;
using Stockhog.Application.Abstractions.Results;
using Microsoft.Extensions.DependencyInjection;

namespace Stockhog.Application.Messaging;

/// <summary>
/// Resolves the handler and pipeline behaviors for a request and runs them in a fresh DI scope,
/// so scoped services (later: the DbContext) live exactly as long as one command or query.
/// </summary>
internal sealed class Dispatcher(IServiceScopeFactory scopeFactory) : IDispatcher
{
    // Reflection runs once per request type; afterwards dispatch is a dictionary lookup plus virtual calls.
    private static readonly ConcurrentDictionary<Type, RequestHandlerWrapper> Wrappers = new();

    public async Task<Result<TResponse>> SendAsync<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var wrapper = (RequestHandlerWrapper<TResponse>)Wrappers.GetOrAdd(request.GetType(), CreateWrapper);

        await using var scope = scopeFactory.CreateAsyncScope();
        return await wrapper.HandleAsync(request, scope.ServiceProvider, cancellationToken).ConfigureAwait(false);
    }

    private static RequestHandlerWrapper CreateWrapper(Type requestType)
    {
        var responseType = requestType.GetInterfaces()
            .Single(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IRequest<>))
            .GetGenericArguments()[0];

        var wrapperType = typeof(RequestHandlerWrapper<,>).MakeGenericType(requestType, responseType);
        return (RequestHandlerWrapper)Activator.CreateInstance(wrapperType)!;
    }
}

internal abstract class RequestHandlerWrapper;

internal abstract class RequestHandlerWrapper<TResponse> : RequestHandlerWrapper
{
    public abstract Task<Result<TResponse>> HandleAsync(IRequest<TResponse> request, IServiceProvider services, CancellationToken cancellationToken);
}

internal sealed class RequestHandlerWrapper<TRequest, TResponse> : RequestHandlerWrapper<TResponse>
    where TRequest : IRequest<TResponse>
{
    public override Task<Result<TResponse>> HandleAsync(IRequest<TResponse> request, IServiceProvider services, CancellationToken cancellationToken)
    {
        var typedRequest = (TRequest)request;
        var handler = services.GetService<IRequestHandler<TRequest, TResponse>>()
                      ?? throw new InvalidOperationException($"No handler is registered for {typeof(TRequest).Name}.");

        RequestHandlerDelegate<TResponse> pipeline = () => handler.HandleAsync(typedRequest, cancellationToken);

        // Wrap from the inside out so the first registered behavior runs first.
        foreach (var behavior in services.GetServices<IPipelineBehavior<TRequest, TResponse>>().Reverse())
        {
            var next = pipeline;
            pipeline = () => behavior.HandleAsync(typedRequest, next, cancellationToken);
        }

        return pipeline();
    }
}
