using Stockhog.Application.Abstractions.Messaging;
using Stockhog.Application.Abstractions.Results;
using Stockhog.Application.Messaging;
using Microsoft.Extensions.DependencyInjection;

namespace Stockhog.Application.Tests.Messaging;

public class DispatcherTests
{
    [Fact]
    public async Task Routes_request_to_its_handler()
    {
        await using var provider = CreateProvider();

        var result = await provider.GetRequiredService<IDispatcher>().SendAsync(new Ping("hi"), TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.StartsWith("pong:hi", result.Value);
    }

    [Fact]
    public async Task Behaviors_run_in_registration_order_around_the_handler()
    {
        await using var provider = CreateProvider();

        await provider.GetRequiredService<IDispatcher>().SendAsync(new Ping("x"), TestContext.Current.CancellationToken);

        Assert.Equal(["outer>", "inner>", "handler", "<inner", "<outer"], provider.GetRequiredService<CallLog>().Entries);
    }

    [Fact]
    public async Task Each_request_gets_its_own_scope()
    {
        await using var provider = CreateProvider();
        var dispatcher = provider.GetRequiredService<IDispatcher>();

        var first = await dispatcher.SendAsync(new Ping("a"), TestContext.Current.CancellationToken);
        var second = await dispatcher.SendAsync(new Ping("b"), TestContext.Current.CancellationToken);

        // The scoped marker is shared by behavior and handler within a request, but differs between requests.
        var log = provider.GetRequiredService<CallLog>();
        Assert.Equal(first.Value.Split(':')[2], log.ScopeIds[0]);
        Assert.Equal(second.Value.Split(':')[2], log.ScopeIds[1]);
        Assert.NotEqual(log.ScopeIds[0], log.ScopeIds[1]);
    }

    [Fact]
    public async Task Missing_handler_is_a_configuration_error()
    {
        await using var provider = CreateProvider();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            provider.GetRequiredService<IDispatcher>().SendAsync(new Unhandled(), TestContext.Current.CancellationToken));
    }

    private static ServiceProvider CreateProvider()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IDispatcher, Dispatcher>();
        services.AddSingleton<CallLog>();
        services.AddScoped<ScopeMarker>();
        services.AddTransient(typeof(IPipelineBehavior<,>), typeof(OuterBehavior<,>));
        services.AddTransient(typeof(IPipelineBehavior<,>), typeof(InnerBehavior<,>));
        services.AddTransient<IRequestHandler<Ping, string>, PingHandler>();
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    }

    public sealed record Ping(string Text) : IQuery<string>;

    public sealed record Unhandled : IQuery<string>;

    internal sealed class CallLog
    {
        public List<string> Entries { get; } = [];
        public List<string> ScopeIds { get; } = [];
    }

    internal sealed class ScopeMarker
    {
        public string Id { get; } = Guid.NewGuid().ToString("N");
    }

    internal sealed class PingHandler(CallLog log, ScopeMarker scope) : IQueryHandler<Ping, string>
    {
        public Task<Result<string>> HandleAsync(Ping request, CancellationToken cancellationToken)
        {
            log.Entries.Add("handler");
            return Task.FromResult<Result<string>>($"pong:{request.Text}:{scope.Id}");
        }
    }

    internal sealed class OuterBehavior<TRequest, TResponse>(CallLog log, ScopeMarker scope) : IPipelineBehavior<TRequest, TResponse>
        where TRequest : IRequest<TResponse>
    {
        public async Task<Result<TResponse>> HandleAsync(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken)
        {
            log.ScopeIds.Add(scope.Id);
            log.Entries.Add("outer>");
            var result = await next();
            log.Entries.Add("<outer");
            return result;
        }
    }

    internal sealed class InnerBehavior<TRequest, TResponse>(CallLog log) : IPipelineBehavior<TRequest, TResponse>
        where TRequest : IRequest<TResponse>
    {
        public async Task<Result<TResponse>> HandleAsync(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken)
        {
            log.Entries.Add("inner>");
            var result = await next();
            log.Entries.Add("<inner");
            return result;
        }
    }
}
