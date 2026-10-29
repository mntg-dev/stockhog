using Stockhog.Application.Backtesting;
using Stockhog.Application.Backtesting.Strategies;
using Stockhog.Application.MarketData.GetPriceBars;
using Stockhog.Infrastructure.Http;
using Stockhog.Infrastructure.Lean.Data;
using Stockhog.Infrastructure.Lean.Engine;
using Stockhog.Infrastructure.Lean.Engine.Results;
using Stockhog.Infrastructure.Lean.Strategies;
using Stockhog.Infrastructure.MarketData;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Stockhog.Infrastructure.Lean;

public static class LeanDependencyInjection
{
    /// <summary>
    /// Lean as the backtest engine, result reader, strategy catalog and <see cref="IMarketDataStore"/>.
    /// </summary>
    public static IServiceCollection AddLeanEngine(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<LeanOptions>()
            .Bind(configuration.GetSection(LeanOptions.SectionName));

        services.AddHttpClient<LeanReferenceDataClient>((sp, http) =>
            {
                var options = sp.GetRequiredService<IOptions<LeanOptions>>().Value;
                http.BaseAddress = new Uri(options.ReferenceDataBaseUrl.TrimEnd('/') + "/");
            })
            .ConfigurePrimaryHttpMessageHandler(HttpClientDefaults.CreatePrimaryHandler)
            .AddStandardResilienceHandler(HttpClientDefaults.ConfigureResilience);

        services.AddSingleton<LeanEquityDataWriter>();
        services.AddTransient<IMarketDataStore, LeanMarketDataStore>();
        services.AddSingleton<IPriceBarReader, LeanPriceBarReader>();

        services.AddSingleton<IBacktestEngine, LeanBacktestEngine>();
        services.AddSingleton<IBacktestResultReader, LeanResultReader>();
        services.AddSingleton<IStrategyCatalog, LeanStrategyCatalog>();

        services.AddHostedService<LeanDataFolderInitializer>();
        return services;
    }
}
