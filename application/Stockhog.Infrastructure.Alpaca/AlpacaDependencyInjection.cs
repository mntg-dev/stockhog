using Stockhog.Application.Instruments;
using Stockhog.Application.Trading;
using Stockhog.Application.Trading.Safety;
using Stockhog.Infrastructure.Alpaca.Instruments;
using Stockhog.Infrastructure.Alpaca.MarketData;
using Stockhog.Infrastructure.Alpaca.Streaming;
using Stockhog.Infrastructure.Alpaca.Trading;
using Stockhog.Infrastructure.Http;
using Stockhog.Infrastructure.MarketData;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Options;

namespace Stockhog.Infrastructure.Alpaca;

public static class AlpacaDependencyInjection
{
    /// <summary>
    /// Alpaca as the <see cref="IHistoricalBarSource"/>.
    /// </summary>
    public static IServiceCollection AddAlpacaMarketData(this IServiceCollection services, IConfiguration configuration)
    {
        // Credentials are optional at startup so backtests still work without them; the client checks them on use.
        services.AddOptions<AlpacaOptions>()
            .Bind(configuration.GetSection(AlpacaOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<AlpacaOptions>, TradingModeValidation>();

        services.AddSingleton<AlpacaRateLimiter>();
        services.AddTransient<AlpacaRateLimitingHandler>();

        // Outer to inner: resilience (timeouts, retries, circuit breaker), then the rate limiter,
        // so every attempt including retries waits for a token.
        var client = services.AddHttpClient<IHistoricalBarSource, AlpacaMarketDataClient>(ConfigureDataClient)
            .ConfigurePrimaryHttpMessageHandler(HttpClientDefaults.CreatePrimaryHandler);
        client.AddStandardResilienceHandler(HttpClientDefaults.ConfigureResilience);
        client.AddHttpMessageHandler<AlpacaRateLimitingHandler>();

        // The asset list lives on the trading API (paper or live host, same list) and uses the trading keys.
        var assets = services.AddHttpClient<IInstrumentSource, AlpacaAssetsClient>((sp, http) =>
                http.BaseAddress = new Uri(sp.GetRequiredService<IOptions<AlpacaOptions>>().Value.TradingBaseUrl.TrimEnd('/') + "/"))
            .ConfigurePrimaryHttpMessageHandler(HttpClientDefaults.CreatePrimaryHandler);
        assets.AddStandardResilienceHandler(HttpClientDefaults.ConfigureResilience);
        assets.AddHttpMessageHandler<AlpacaRateLimitingHandler>();

        return services;
    }

    /// <summary>
    /// Trading through Alpaca (paper unless live is fully opted into): the broker gateway, the trade update and market
    /// data streams, periodic reconciliation and the daily loss guard. Requires <see cref="AddAlpacaMarketData"/>.
    /// </summary>
    public static IServiceCollection AddAlpacaTrading(this IServiceCollection services)
    {
        var broker = services.AddHttpClient<IBrokerGateway, AlpacaBrokerClient>((sp, http) =>
                http.BaseAddress = new Uri(sp.GetRequiredService<IOptions<AlpacaOptions>>().Value.TradingBaseUrl.TrimEnd('/') + "/"))
            .ConfigurePrimaryHttpMessageHandler(HttpClientDefaults.CreatePrimaryHandler);
        broker.AddStandardResilienceHandler(options =>
        {
            HttpClientDefaults.ConfigureResilience(options);
            options.TotalRequestTimeout.Timeout = TimeSpan.FromSeconds(30);

            // Never resend an order or a cancel: a timed-out POST may have been carried out. Reconciliation finds out.
            options.Retry.DisableForUnsafeHttpMethods();
        });
        broker.AddHttpMessageHandler<AlpacaRateLimitingHandler>();

        var latest = services.AddHttpClient<ILatestPriceSource, AlpacaLatestPriceClient>(ConfigureDataClient)
            .ConfigurePrimaryHttpMessageHandler(HttpClientDefaults.CreatePrimaryHandler);
        latest.AddStandardResilienceHandler(HttpClientDefaults.ConfigureResilience);
        latest.AddHttpMessageHandler<AlpacaRateLimitingHandler>();

        services.AddSingleton<StreamSocketFactory>(() => new ClientWebSocketStream());
        services.AddSingleton<ConnectionMonitor>();
        services.AddSingleton<IConnectionMonitor>(sp => sp.GetRequiredService<ConnectionMonitor>());

        services.AddSingleton<AlpacaMarketDataStream>();
        services.AddSingleton<ILiveMarketData>(sp => sp.GetRequiredService<AlpacaMarketDataStream>());
        services.AddHostedService(sp => sp.GetRequiredService<AlpacaMarketDataStream>());
        services.AddHostedService<AlpacaTradeUpdatesStream>();
        services.AddHostedService<OrderReconciliationService>();
        services.AddHostedService<DailyLossGuardService>();
        services.AddSingleton<ITradingEnvironment, AlpacaTradingEnvironment>();

        return services;
    }

    private static void ConfigureDataClient(IServiceProvider services, HttpClient http)
    {
        var options = services.GetRequiredService<IOptions<AlpacaOptions>>().Value;
        http.BaseAddress = new Uri(options.DataBaseUrl.TrimEnd('/') + "/");
    }
}
