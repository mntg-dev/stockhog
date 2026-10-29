using Stockhog.Application.Abstractions.Messaging;
using Stockhog.Application.Abstractions.Results;
using Stockhog.Application.Behaviors;
using Stockhog.Application.Messaging;
using FluentValidation;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Stockhog.Application.Tests.Behaviors;

public class ValidationBehaviorTests
{
    [Fact]
    public async Task Invalid_request_short_circuits_with_all_messages()
    {
        await using var provider = CreateProvider();

        var result = await provider.GetRequiredService<IDispatcher>().SendAsync(new Lookup("", -1), TestContext.Current.CancellationToken);

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorType.Validation, result.Error.Type);
        Assert.Equal(["Key is required.", "Version must not be negative."], result.Error.Details);
        Assert.Equal(0, provider.GetRequiredService<LookupHandler.Counter>().Calls);
    }

    [Fact]
    public async Task Valid_request_reaches_the_handler()
    {
        await using var provider = CreateProvider();

        var result = await provider.GetRequiredService<IDispatcher>().SendAsync(new Lookup("k", 1), TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
    }

    private static ServiceProvider CreateProvider() => TestPipeline.Create(services =>
    {
        services.AddTransient(typeof(IPipelineBehavior<,>), typeof(ValidationBehavior<,>));
        services.AddTransient<IValidator<Lookup>, LookupValidator>();
    });

    private sealed class LookupValidator : AbstractValidator<Lookup>
    {
        public LookupValidator()
        {
            RuleFor(l => l.Key).NotEmpty().WithMessage("Key is required.");
            RuleFor(l => l.Version).GreaterThanOrEqualTo(0).WithMessage("Version must not be negative.");
        }
    }
}

public class CachingBehaviorTests
{
    [Fact]
    public async Task Second_identical_query_is_served_from_cache()
    {
        await using var provider = CreateProvider();
        var dispatcher = provider.GetRequiredService<IDispatcher>();
        var ct = TestContext.Current.CancellationToken;

        var first = await dispatcher.SendAsync(new Lookup("k", 1), ct);
        var second = await dispatcher.SendAsync(new Lookup("k", 1), ct);

        Assert.Equal(first.Value, second.Value);
        Assert.Equal(1, provider.GetRequiredService<LookupHandler.Counter>().Calls);
    }

    [Fact]
    public async Task Failures_are_not_cached()
    {
        await using var provider = CreateProvider();
        var dispatcher = provider.GetRequiredService<IDispatcher>();
        var ct = TestContext.Current.CancellationToken;

        var first = await dispatcher.SendAsync(new Lookup("missing", 1), ct);
        var second = await dispatcher.SendAsync(new Lookup("missing", 1), ct);

        Assert.Equal(ErrorType.NotFound, first.Error?.Type);
        Assert.Equal(ErrorType.NotFound, second.Error?.Type);
        Assert.Equal(2, provider.GetRequiredService<LookupHandler.Counter>().Calls);
    }

    [Fact]
    public async Task Removing_a_tag_invalidates_its_entries()
    {
        await using var provider = CreateProvider();
        var dispatcher = provider.GetRequiredService<IDispatcher>();
        var ct = TestContext.Current.CancellationToken;

        await dispatcher.SendAsync(new Lookup("k", 1), ct);
        await provider.GetRequiredService<HybridCache>().RemoveByTagAsync("lookups", ct);
        await dispatcher.SendAsync(new Lookup("k", 1), ct);

        Assert.Equal(2, provider.GetRequiredService<LookupHandler.Counter>().Calls);
    }

    [Fact]
    public async Task Concurrent_callers_for_the_same_key_share_one_handler_execution()
    {
        await using var provider = CreateProvider(handlerDelay: TimeSpan.FromMilliseconds(200));
        var dispatcher = provider.GetRequiredService<IDispatcher>();
        var ct = TestContext.Current.CancellationToken;

        var results = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => dispatcher.SendAsync(new Lookup("k", 1), ct)));

        Assert.All(results, r => Assert.True(r.IsSuccess));
        Assert.Equal(1, provider.GetRequiredService<LookupHandler.Counter>().Calls);
    }

    [Fact]
    public async Task Requests_that_are_not_cacheable_always_reach_the_handler()
    {
        await using var provider = CreateProvider();
        var dispatcher = provider.GetRequiredService<IDispatcher>();
        var ct = TestContext.Current.CancellationToken;

        await dispatcher.SendAsync(new UncachedLookup("k"), ct);
        await dispatcher.SendAsync(new UncachedLookup("k"), ct);

        Assert.Equal(2, provider.GetRequiredService<LookupHandler.Counter>().Calls);
    }

    private static ServiceProvider CreateProvider(TimeSpan handlerDelay = default) => TestPipeline.Create(services =>
    {
        services.AddHybridCache();
        services.AddTransient(typeof(IPipelineBehavior<,>), typeof(CachingBehavior<,>));
        services.AddSingleton(new LookupHandler.Options(handlerDelay));
    });
}

public sealed record Lookup(string Key, int Version) : IQuery<string>, ICacheableQuery
{
    public string CacheKey => $"lookup:{Key}:{Version}";
    public IReadOnlyCollection<string> CacheTags => ["lookups"];
    public TimeSpan CacheDuration => TimeSpan.FromMinutes(5);
}

public sealed record UncachedLookup(string Key) : IQuery<string>;

internal sealed class LookupHandler(LookupHandler.Counter counter, LookupHandler.Options options)
    : IQueryHandler<Lookup, string>, IQueryHandler<UncachedLookup, string>
{
    public async Task<Result<string>> HandleAsync(Lookup request, CancellationToken cancellationToken)
    {
        counter.Increment();
        if (options.Delay > TimeSpan.Zero)
            await Task.Delay(options.Delay, cancellationToken);

        return request.Key == "missing"
            ? Error.NotFound("Lookup.Missing", "Not found.")
            : $"{request.Key}-{Guid.NewGuid():N}";
    }

    public Task<Result<string>> HandleAsync(UncachedLookup request, CancellationToken cancellationToken)
    {
        counter.Increment();
        return Task.FromResult<Result<string>>(request.Key);
    }

    internal sealed class Counter
    {
        private int _calls;
        public int Calls => _calls;
        public void Increment() => Interlocked.Increment(ref _calls);
    }

    internal sealed record Options(TimeSpan Delay);
}

internal static class TestPipeline
{
    public static ServiceProvider Create(Action<IServiceCollection> configure)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IDispatcher, Dispatcher>();
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddSingleton<LookupHandler.Counter>();
        services.AddSingleton(new LookupHandler.Options(TimeSpan.Zero));
        services.AddTransient<IRequestHandler<Lookup, string>, LookupHandler>();
        services.AddTransient<IRequestHandler<UncachedLookup, string>, LookupHandler>();
        configure(services);
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    }
}
