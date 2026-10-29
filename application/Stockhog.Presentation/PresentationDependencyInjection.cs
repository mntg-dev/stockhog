using CommunityToolkit.Mvvm.Messaging;
using Stockhog.Presentation.Analytics;
using Stockhog.Application.Assistant;
using Stockhog.Presentation.Assistant;
using Stockhog.Presentation.Backtesting;
using Stockhog.Presentation.Compare;
using Stockhog.Presentation.Guide;
using Stockhog.Presentation.Instruments;
using Stockhog.Presentation.Jobs;
using Stockhog.Presentation.Logging;
using Stockhog.Presentation.MarketData;
using Stockhog.Presentation.Optimization;
using Stockhog.Presentation.Settings;
using Stockhog.Presentation.Shell;
using Stockhog.Presentation.Strategies;
using Stockhog.Presentation.Trading;
using Microsoft.Extensions.DependencyInjection;

namespace Stockhog.Presentation;

public static class PresentationDependencyInjection
{
    /// <summary>
    /// View models. The host must also register <see cref="UserSettings"/>, <see cref="IUserSettingsStore"/>,
    /// <see cref="AppEnvironmentInfo"/> and <see cref="Common.IUserConfirmation"/>. Pages are singletons so their state survives navigation.
    /// </summary>
    public static IServiceCollection AddPresentationServices(this IServiceCollection services)
    {
        services.AddSingleton<IMessenger>(WeakReferenceMessenger.Default);
        services.AddSingleton<StatusViewModel>();
        services.AddSingleton<LogFeed>();
        services.AddSingleton<InstrumentCatalog>();
        services.AddSingleton<DownloadedSymbolCatalog>();

        services.AddSingleton<BacktestViewModel>();
        services.AddSingleton<StrategiesViewModel>();
        services.AddSingleton<HistoryViewModel>();
        services.AddSingleton<CompareViewModel>();
        services.AddSingleton<AssistantViewModel>();
        services.AddSingleton<IAssistantActions>(sp => sp.GetRequiredService<AssistantViewModel>());
        services.AddSingleton<OptimizationViewModel>();
        services.AddSingleton<AnalysisViewModel>();
        services.AddSingleton<TradingViewModel>();
        services.AddSingleton<MarketDataViewModel>();
        services.AddSingleton<JobsViewModel>();
        services.AddSingleton<LogsViewModel>();
        services.AddSingleton<GuideViewModel>();
        services.AddSingleton<SettingsViewModel>();
        services.AddSingleton<ShellViewModel>();
        return services;
    }
}
