using Stockhog.Application.Abstractions.Jobs;
using Stockhog.Application.Abstractions.Persistence;
using Stockhog.Application.Backtesting;
using Stockhog.Application.Instruments;
using Stockhog.Application.MarketData;
using Stockhog.Application.Optimization;
using Stockhog.Application.Trading;
using Stockhog.Application.Trading.Safety;
using Stockhog.Infrastructure.Configuration;
using Stockhog.Infrastructure.Jobs;
using Stockhog.Infrastructure.MarketData;
using Stockhog.Infrastructure.Persistence;
using Stockhog.Infrastructure.Trading;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Stockhog.Infrastructure;

public static class InfrastructureDependencyInjection
{
    /// <summary>
    /// Shared infrastructure: storage settings, persistence, background jobs, caching and the market data downloader.
    /// The downloader needs an <see cref="IHistoricalBarSource"/> and an <see cref="IMarketDataStore"/>,
    /// registered by the Alpaca and Lean adapter projects.
    /// </summary>
    public static IServiceCollection AddInfrastructureServices(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<StorageOptions>()
            .Bind(configuration.GetSection(StorageOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();
        services.AddSingleton<IPostConfigureOptions<StorageOptions>, StorageOptionsPostConfigure>();

        services.TryAddSingleton(TimeProvider.System);
        services.AddHybridCache();

        services.AddTransient<IMarketDataDownloader, MarketDataDownloader>();

        AddPersistence(services);
        AddJobs(services, configuration);
        AddTrading(services, configuration);

        return services;
    }

    private static void AddPersistence(IServiceCollection services)
    {
        services.AddDbContext<TradingDbContext>((sp, options) =>
        {
            var databaseFile = sp.GetRequiredService<IOptions<StorageOptions>>().Value.DatabaseFile;
            Directory.CreateDirectory(Path.GetDirectoryName(databaseFile)!);
            options.UseSqlite(new SqliteConnectionStringBuilder { DataSource = databaseFile }.ToString());
        });

        services.AddScoped<IUnitOfWork, UnitOfWork>();
        services.AddScoped<IBacktestRunRepository, BacktestRunRepository>();
        services.AddScoped<IBacktestHistoryReader, BacktestHistoryReader>();
        services.AddScoped<IMarketDataSetRepository, MarketDataSetRepository>();
        services.AddScoped<IMarketDataCoverageReader, MarketDataCoverageReader>();
        services.AddScoped<IOptimizationRunRepository, OptimizationRunRepository>();
        services.AddScoped<IOptimizationReader, OptimizationReader>();
        services.AddScoped<IOrderRepository, OrderRepository>();
        services.AddScoped<IOrderReader, OrderReader>();
        services.AddScoped<IInstrumentStore, InstrumentStore>();

        services.AddHostedService<DatabaseInitializer>();
    }

    /// <summary>Broker-independent trading services; the broker itself is registered by its adapter.</summary>
    private static void AddTrading(IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<RiskOptions>()
            .Bind(configuration.GetSection(RiskOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddSingleton<IRiskLimitsStore, RiskLimitsStore>();
        services.AddSingleton<IAuditLog, AuditLog>();
        services.AddSingleton<QuoteCache>();
        services.AddSingleton<IQuoteCache>(sp => sp.GetRequiredService<QuoteCache>());
    }

    private static void AddJobs(IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<JobOptions>()
            .Bind(configuration.GetSection(JobOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddSingleton<JobQueue>();
        services.AddSingleton<IJobQueue>(sp => sp.GetRequiredService<JobQueue>());
        services.AddSingleton<IJobMonitor>(sp => sp.GetRequiredService<JobQueue>());

        // Registered after DatabaseInitializer: jobs may use the database.
        services.AddHostedService<JobProcessor>();

        // Needs the database and the job queue, so it starts after both.
        services.AddHostedService<StartupJobScheduler>();
    }
}
