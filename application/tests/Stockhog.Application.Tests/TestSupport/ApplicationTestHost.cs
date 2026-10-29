using Stockhog.Application.Abstractions.Jobs;
using Stockhog.Application.Abstractions.Messaging;
using Stockhog.Application.Abstractions.Persistence;
using Stockhog.Application.Assistant;
using Stockhog.Application.Backtesting;
using Stockhog.Application.Backtesting.Strategies;
using Stockhog.Application.Instruments;
using Stockhog.Application.MarketData;
using Stockhog.Application.MarketData.GetPriceBars;
using Stockhog.Application.Optimization;
using Stockhog.Application.Trading;
using Stockhog.Application.Trading.Safety;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Stockhog.Application.Tests.TestSupport;

/// <summary>
/// The real Application registration (assembly scanning, behaviors, validators, domain events) with fake ports.
/// <c>ValidateOnBuild</c> fails if any handler needs a service nobody registered.
/// </summary>
internal sealed class ApplicationTestHost : IAsyncDisposable
{
    private readonly ServiceProvider _provider;

    public ApplicationTestHost()
    {
        var services = new ServiceCollection();
        services.AddApplicationServices();
        services.AddHybridCache();
        services.AddSingleton<TimeProvider>(Clock);
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));

        services.AddSingleton<IBacktestEngine>(Engine);
        services.AddSingleton<IBacktestResultReader>(Reader);
        services.AddSingleton<IMarketDataDownloader>(Downloader);
        services.AddSingleton<IJobQueue>(Jobs);
        services.AddSingleton<IStrategyCatalog>(Strategies);
        services.AddSingleton<IPriceBarReader>(Prices);

        services.AddSingleton(Database);
        services.AddSingleton<IBacktestRunRepository>(Database);
        services.AddSingleton<IBacktestHistoryReader>(Database);
        services.AddSingleton<IMarketDataSetRepository>(Database);
        services.AddSingleton<IMarketDataCoverageReader>(Database);
        services.AddSingleton<IOptimizationRunRepository>(Database);
        services.AddSingleton<IOptimizationReader>(Database);
        services.AddSingleton<IOrderRepository>(Database);
        services.AddSingleton<IOrderReader>(Database);
        services.AddSingleton<IBrokerGateway>(Broker);
        services.AddSingleton<IQuoteCache>(Quotes);
        services.AddSingleton<ILatestPriceSource>(LatestPrices);
        services.AddSingleton<IRiskLimitsStore>(RiskLimits);
        services.AddSingleton<ITradingEnvironment>(Environment);
        services.AddSingleton<IAuditLog>(AuditLog);
        services.AddSingleton<IInstrumentSource>(InstrumentSource);
        services.AddSingleton<IInstrumentStore>(Instruments);
        services.AddSingleton<IAssistantCredentials>(AssistantCredentials);
        services.AddSingleton<IAssistantClient>(new FakeAssistantClient(AssistantCredentials));
        services.AddSingleton<IAssistantActions>(AssistantActions);
        services.AddScoped<IUnitOfWork, InMemoryUnitOfWork>();

        _provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
    }

    public FakeTimeProvider Clock { get; } = new(TestData.Now);
    public FakeBacktestEngine Engine { get; } = new();
    public CountingResultReader Reader { get; } = new();
    public FakeMarketDataDownloader Downloader { get; } = new();
    public InlineJobQueue Jobs { get; } = new();
    public FakeStrategyCatalog Strategies { get; } = new();
    public FakePriceBarReader Prices { get; } = new();
    public InMemoryDatabase Database { get; } = new();
    public FakeBrokerGateway Broker { get; } = new();
    public FakeQuoteCache Quotes { get; } = new();
    public FakeLatestPriceSource LatestPrices { get; } = new();
    public FakeRiskLimitsStore RiskLimits { get; } = new();
    public FakeTradingEnvironment Environment { get; } = new();
    public InMemoryAuditLog AuditLog { get; } = new();
    public FakeInstrumentSource InstrumentSource { get; } = new();
    public InMemoryInstrumentStore Instruments { get; } = new();
    public InMemoryAssistantCredentials AssistantCredentials { get; } = new();
    public RecordingAssistantActions AssistantActions { get; } = new();

    public IAssistantToolbox Toolbox => _provider.GetRequiredService<IAssistantToolbox>();

    public IDispatcher Dispatcher => _provider.GetRequiredService<IDispatcher>();

    public ValueTask DisposeAsync() => _provider.DisposeAsync();
}
