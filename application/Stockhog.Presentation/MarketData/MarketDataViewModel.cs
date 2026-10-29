using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Stockhog.Application.Abstractions.Messaging;
using Stockhog.Application.Instruments;
using Stockhog.Application.Instruments.QueueInstrumentRefresh;
using Stockhog.Application.MarketData;
using Stockhog.Application.MarketData.GetMarketDataCoverage;
using Stockhog.Application.MarketData.GetPriceBars;
using Stockhog.Application.MarketData.QueueMarketDataDownloads;
using Stockhog.Domain.MarketData;
using Stockhog.Presentation.Localization;
using Stockhog.Presentation.Settings;
using Stockhog.Presentation.Shell;
using Stockhog.Wpf.Controls.Charts;
using Microsoft.Extensions.Logging;

namespace Stockhog.Presentation.MarketData;

/// <summary>
/// Download data for a symbol list (background jobs), see what is stored, and preview it as a candlestick chart.
/// </summary>
public sealed partial class MarketDataViewModel : PageViewModel, IRecipient<MarketDataChangedMessage>, IRecipient<InstrumentsChangedMessage>
{
    private static readonly char[] SymbolSeparators = [',', ';', ' '];

    private readonly IDispatcher _dispatcher;

    public MarketDataViewModel(IDispatcher dispatcher, IMessenger messenger, StatusViewModel status, UserSettings settings, ILogger<MarketDataViewModel> logger)
        : base(status, logger)
    {
        _dispatcher = dispatcher;
        _symbolList = settings.DefaultSymbols;
        messenger.RegisterAll(this);
    }

    public IReadOnlyList<Timeframe> Timeframes { get; } = Enum.GetValues<Timeframe>();

    [ObservableProperty] private string _symbolList;
    [ObservableProperty] private Timeframe _selectedTimeframe = Timeframe.Minute;
    [ObservableProperty] private DateTime? _startDate = DateTime.Today.AddYears(-1);
    [ObservableProperty] private DateTime? _endDate = DateTime.Today;

    [ObservableProperty] private IReadOnlyList<MarketDataCoverage> _coverage = [];
    [ObservableProperty] private MarketDataCoverage? _selectedCoverage;
    [ObservableProperty] private IReadOnlyList<PriceBar> _previewBars = [];
    [ObservableProperty] private string? _previewTitle;

    /// <summary>How many instruments the symbol pickers know and how fresh the list is.</summary>
    [ObservableProperty] private string? _symbolsStatus;

    protected override async Task LoadAsync()
    {
        await RefreshCoverageAsync(CancellationToken.None);
        await LoadSymbolsStatusAsync();
    }

    public async void Receive(MarketDataChangedMessage message)
    {
        await RefreshCoverageAsync(CancellationToken.None);
        await LoadPreviewAsync(SelectedCoverage);
    }

    public async void Receive(InstrumentsChangedMessage message) => await LoadSymbolsStatusAsync();

    /// <summary>Downloads the instrument list in the background; the pickers reload it when the job finishes.</summary>
    [RelayCommand]
    private async Task RefreshSymbolsAsync(CancellationToken cancellationToken)
    {
        var result = await _dispatcher.SendAsync(new QueueInstrumentRefreshCommand(), cancellationToken);
        if (result.IsFailure)
            Status.Show(result.Error);
        else
            Status.Show(Loc.Get(result.Value.WasQueued ? "Status_QueuedSymbolRefresh" : "Status_SymbolRefreshRunning"));
    }

    private async Task LoadSymbolsStatusAsync()
    {
        var status = await _dispatcher.SendAsync(new GetInstrumentCatalogStatusQuery());
        if (status.IsFailure)
        {
            Status.Show(status.Error);
            return;
        }

        SymbolsStatus = status.Value is { Count: > 0, RefreshedAt: { } refreshedAt }
            ? Loc.Format("MarketData_SymbolsStatus", status.Value.Count, refreshedAt.LocalDateTime)
            : Loc.Get("MarketData_SymbolsMissing");
    }

    /// <summary>Queues one job per symbol and returns at once; the jobs page shows their progress.</summary>
    [RelayCommand]
    private async Task DownloadAsync(CancellationToken cancellationToken)
    {
        if (StartDate is null || EndDate is null)
        {
            Status.Show(Loc.Get("Status_SelectDates"));
            return;
        }

        var symbols = SymbolList.Split(SymbolSeparators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var result = await _dispatcher.SendAsync(new QueueMarketDataDownloadsCommand(
            symbols, SelectedTimeframe, DateOnly.FromDateTime(StartDate.Value), DateOnly.FromDateTime(EndDate.Value)), cancellationToken);

        if (result.IsFailure)
            Status.Show(result.Error);
        else if (result.Value.AlreadyQueued.Count == 0)
            Status.Show(Loc.Format("Status_QueuedDownloads", result.Value.JobIds.Count));
        else
            Status.Show(Loc.Format("Status_QueuedDownloadsSkipped", result.Value.JobIds.Count, string.Join(", ", result.Value.AlreadyQueued)));
    }

    [RelayCommand]
    private async Task RefreshCoverageAsync(CancellationToken cancellationToken)
    {
        var coverage = await _dispatcher.SendAsync(new GetMarketDataCoverageQuery(), cancellationToken);
        if (coverage.IsFailure)
        {
            Status.Show(coverage.Error);
            return;
        }

        var selected = SelectedCoverage;
        Coverage = coverage.Value;
        SelectedCoverage = Coverage.FirstOrDefault(c => c.Symbol == selected?.Symbol && c.Timeframe == selected.Timeframe);
    }

    partial void OnSelectedCoverageChanged(MarketDataCoverage? value) => _ = LoadPreviewAsync(value);

    /// <summary>Daily candles for daily data, hourly candles otherwise (minute data is aggregated).</summary>
    private async Task LoadPreviewAsync(MarketDataCoverage? coverage)
    {
        if (coverage is null || coverage.Ranges.Count == 0)
        {
            PreviewBars = [];
            PreviewTitle = null;
            return;
        }

        var resolution = coverage.Timeframe == Timeframe.Daily ? BarResolution.Daily : BarResolution.Hourly;
        var span = resolution == BarResolution.Daily ? TimeSpan.FromDays(1) : TimeSpan.FromHours(1);
        var result = await _dispatcher.SendAsync(new GetPriceBarsQuery(coverage.Symbol, resolution, coverage.Ranges[0].Start, coverage.Ranges[^1].End));
        if (result.IsFailure)
        {
            Status.Show(result.Error);
            return;
        }

        PreviewBars = result.Value.Bars.Select(b => new PriceBar(b.Time, span, (double)b.Open, (double)b.High, (double)b.Low, (double)b.Close)).ToList();
        PreviewTitle = Loc.Format("MarketData_PreviewTitle", coverage.Symbol, Loc.Get($"Resolution_{resolution}"), PreviewBars.Count);
    }
}
