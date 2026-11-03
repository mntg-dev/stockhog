using System.ComponentModel;
using System.Windows.Data;
using System.Windows.Markup;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;
using Stockhog.Presentation.Analytics;
using Stockhog.Presentation.Assistant;
using Stockhog.Presentation.Autopilot;
using Stockhog.Presentation.Backtesting;
using Stockhog.Presentation.Compare;
using Stockhog.Presentation.Guide;
using Stockhog.Presentation.Home;
using Stockhog.Presentation.Jobs;
using Stockhog.Presentation.Localization;
using Stockhog.Presentation.Logging;
using Stockhog.Presentation.MarketData;
using Stockhog.Presentation.Optimization;
using Stockhog.Presentation.Proposals;
using Stockhog.Presentation.Scanning;
using Stockhog.Presentation.Screening;
using Stockhog.Presentation.Settings;
using Stockhog.Presentation.Strategies;
using Stockhog.Presentation.Trading;

namespace Stockhog.Presentation.Shell;

/// <summary>
/// Main window: sidebar navigation between page view models (each rendered through a DataTemplate), plus the shared
/// status bar and the number of active jobs.
/// </summary>
public sealed partial class ShellViewModel : ObservableObject, IRecipient<NavigateMessage>
{
    public ShellViewModel(
        HomeViewModel home,
        BacktestViewModel backtest,
        StrategiesViewModel strategies,
        HistoryViewModel history,
        CompareViewModel compare,
        ScreenerViewModel screener,
        ScannerViewModel scanner,
        ProposalsViewModel proposals,
        AutopilotViewModel autopilot,
        AssistantViewModel assistant,
        OptimizationViewModel optimization,
        AnalysisViewModel analysis,
        TradingViewModel trading,
        MarketDataViewModel marketData,
        JobsViewModel jobs,
        LogsViewModel logs,
        GuideViewModel guide,
        SettingsViewModel settings,
        StatusViewModel status,
        UpdateBannerViewModel update,
        IMessenger messenger)
    {
        Status = status;
        Update = update;
        Jobs = jobs;
        Pages =
        [
            new NavigationItem("Nav_Home", "", home),
            new NavigationItem("Nav_MarketData", "", marketData, "NavGroup_Research"),
            new NavigationItem("Nav_Screener", "", screener, "NavGroup_Research"),
            new NavigationItem("Nav_Scanner", "", scanner, "NavGroup_Research"),
            new NavigationItem("Nav_Backtest", "", backtest, "NavGroup_Strategies"),
            new NavigationItem("Nav_Strategies", "", strategies, "NavGroup_Strategies"),
            new NavigationItem("Nav_Optimization", "", optimization, "NavGroup_Strategies"),
            new NavigationItem("Nav_Analysis", "", analysis, "NavGroup_Strategies"),
            new NavigationItem("Nav_Compare", "", compare, "NavGroup_Strategies"),
            new NavigationItem("Nav_History", "", history, "NavGroup_Strategies"),
            new NavigationItem("Nav_Proposals", "", proposals, "NavGroup_Trading"),
            new NavigationItem("Nav_Autopilot", "", autopilot, "NavGroup_Trading"),
            new NavigationItem("Nav_Trading", "", trading, "NavGroup_Trading"),
            new NavigationItem("Nav_Assistant", "", assistant, "NavGroup_More"),
            new NavigationItem("Nav_Jobs", "", jobs, "NavGroup_More"),
            new NavigationItem("Nav_Logs", "", logs, "NavGroup_More"),
            new NavigationItem("Nav_Guide", "", guide, "NavGroup_More"),
            new NavigationItem("Nav_Settings", "", settings, "NavGroup_More")
        ];

        // Grouped for the sidebar's section headers; the list itself stays in menu order.
        PagesView = new ListCollectionView(Pages.ToList());
        PagesView.GroupDescriptions.Add(new PropertyGroupDescription(nameof(NavigationItem.GroupKey)));

        messenger.RegisterAll(this);
        Loc.Instance.PropertyChanged += OnLanguageChanged;
        SelectedItem = Pages[0];
    }

    public IReadOnlyList<NavigationItem> Pages { get; }

    public ListCollectionView PagesView { get; }

    public StatusViewModel Status { get; }

    public UpdateBannerViewModel Update { get; }

    public JobsViewModel Jobs { get; }

    public XmlLanguage Language => Loc.Instance.XmlLanguage;

    [ObservableProperty] private NavigationItem? _selectedItem;
    [ObservableProperty] private PageViewModel? _currentPage;

    public string Title => Loc.Get("App_Title");

    partial void OnSelectedItemChanged(NavigationItem? value)
    {
        CurrentPage = value?.Page;
        if (value is not null)
            _ = value.Page.OnNavigatedToAsync();
    }

    public void Receive(NavigateMessage message) =>
        SelectedItem = Pages.FirstOrDefault(p => p.Page.GetType() == message.PageType) ?? SelectedItem;

    /// <summary>
    /// Labels follow the culture through their bindings; numbers and dates formatted by StringFormat use the element
    /// Language, so the current page is rebuilt to pick up the new formats.
    /// </summary>
    private void OnLanguageChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(Loc.Culture))
            return;

        OnPropertyChanged(nameof(Language));
        OnPropertyChanged(nameof(Title));

        var page = CurrentPage;
        CurrentPage = null;
        CurrentPage = page;
    }
}
