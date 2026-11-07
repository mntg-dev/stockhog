using CommunityToolkit.Mvvm.Messaging;
using Stockhog.Presentation.Analytics;
using Stockhog.Application.Assistant;
using Stockhog.Application.Common;
using Stockhog.Presentation.Assistant;
using Stockhog.Presentation.Autopilot;
using Stockhog.Presentation.Backtesting;
using Stockhog.Presentation.Compare;
using Stockhog.Presentation.Guide;
using Stockhog.Presentation.Home;
using Stockhog.Presentation.Instruments;
using Stockhog.Presentation.Jobs;
using Stockhog.Presentation.Logging;
using Stockhog.Presentation.MarketData;
using Stockhog.Presentation.Optimization;
using Stockhog.Presentation.Proposals;
using Stockhog.Presentation.Scanning;
using Stockhog.Presentation.Screening;
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

        services.AddSingleton<UpdateBannerViewModel>();

        // The menu, in order. Pro pages carry their feature; Screener → Scanner → Proposals / Autopilot form the
        // strip "from the market to a trade".
        services.AddPage<HomeViewModel>("Nav_Home", "\ue80f", "", 10);
        services.AddPage<MarketDataViewModel>("Nav_MarketData", "\ue9f9", "NavGroup_Research", 20);
        services.AddPage<ScreenerViewModel>("Nav_Screener", "\ue71c", "NavGroup_Research", 30, feature: ProductFeature.Screener, pipelineKey: "Pipeline_Screener");
        services.AddPage<ScannerViewModel>("Nav_Scanner", "\ue721", "NavGroup_Research", 40, feature: ProductFeature.Scanner, pipelineKey: "Pipeline_Scanner");
        services.AddPage<BacktestViewModel>("Nav_Backtest", "\ue9d9", "NavGroup_Strategies", 50);
        services.AddPage<StrategiesViewModel>("Nav_Strategies", "\ue8f1", "NavGroup_Strategies", 60);
        services.AddPage<OptimizationViewModel>("Nav_Optimization", "\ue9e9", "NavGroup_Strategies", 70, feature: ProductFeature.Optimization);
        services.AddPage<AnalysisViewModel>("Nav_Analysis", "\ue9d2", "NavGroup_Strategies", 80, feature: ProductFeature.RiskAnalysis);
        services.AddPage<CompareViewModel>("Nav_Compare", "\ue8cb", "NavGroup_Strategies", 90);
        services.AddPage<HistoryViewModel>("Nav_History", "\ue81c", "NavGroup_Strategies", 100);
        services.AddPage<ProposalsViewModel>("Nav_Proposals", "\ue73e", "NavGroup_Trading", 110, feature: ProductFeature.Proposals, pipelineKey: "Pipeline_Proposals");
        services.AddPage<AutopilotViewModel>("Nav_Autopilot", "\ue99a", "NavGroup_Trading", 120, feature: ProductFeature.Autopilot, pipelineKey: "Pipeline_Autopilot", pipelineArrow: false);
        services.AddPage<TradingViewModel>("Nav_Trading", "\ue8c7", "NavGroup_Trading", 130);
        services.AddPage<AssistantViewModel>("Nav_Assistant", "\ue8bd", "NavGroup_More", 140, feature: ProductFeature.Assistant);
        services.AddPage<JobsViewModel>("Nav_Jobs", "\ue9f5", "NavGroup_More", 150);
        services.AddPage<LogsViewModel>("Nav_Logs", "\ue7c3", "NavGroup_More", 160);
        services.AddPage<GuideViewModel>("Nav_Guide", "\ue897", "NavGroup_More", 170);
        services.AddPage<SettingsViewModel>("Nav_Settings", "\ue713", "NavGroup_More", 180);

        services.AddSingleton<IAssistantActions>(sp => sp.GetRequiredService<AssistantViewModel>());
        services.AddSingleton<ShellViewModel>();
        return services;
    }
}
