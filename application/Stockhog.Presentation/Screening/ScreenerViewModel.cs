using System.Collections;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Stockhog.Application.Abstractions.Messaging;
using Stockhog.Application.MarketData.QueueMarketDataDownloads;
using Stockhog.Application.Screening;
using Stockhog.Application.Screening.QueueScreener;
using Stockhog.Domain.MarketData;
using Stockhog.Presentation.Compare;
using Stockhog.Presentation.Localization;
using Stockhog.Presentation.Shell;
using Microsoft.Extensions.Logging;

namespace Stockhog.Presentation.Screening;

/// <summary>An exchange the screener can include, as a check box.</summary>
public sealed partial class ExchangeOption(string code, bool isSelected) : ObservableObject
{
    public string Code { get; } = code;

    [ObservableProperty] private bool _isSelected = isSelected;
}

/// <summary>A preset in the picker; its label is the resource <c>ScreenerPreset_{Key}</c>.</summary>
public sealed record PresetOption(ScreenerPreset Preset)
{
    public string TitleKey => $"ScreenerPreset_{Preset.Key}";
}

/// <summary>
/// The first step of the funnel: narrows every listed instrument down to a ranked list by liquidity, price, volatility
/// and momentum (from daily bars), then hands the symbols to the Compare page or downloads their minute data.
/// </summary>
public sealed partial class ScreenerViewModel : PageViewModel, IRecipient<ScreenerRunsChangedMessage>
{
    private readonly IDispatcher _dispatcher;
    private readonly IMessenger _messenger;

    public ScreenerViewModel(IDispatcher dispatcher, IMessenger messenger, StatusViewModel status, ILogger<ScreenerViewModel> logger)
        : base(status, logger)
    {
        _dispatcher = dispatcher;
        _messenger = messenger;
        Exchanges = ScreenerCriteria.AllExchanges.Select(code => new ExchangeOption(code, true)).ToList();
        SelectedPreset = Presets[0];
        messenger.RegisterAll(this);
    }

    public IReadOnlyList<PresetOption> Presets { get; } = ScreenerPreset.All.Select(p => new PresetOption(p)).ToList();

    public IReadOnlyList<ScreenerSort> SortOptions { get; } = Enum.GetValues<ScreenerSort>();

    public IReadOnlyList<ExchangeOption> Exchanges { get; }

    // Criteria; dollar volume is entered in millions.
    [ObservableProperty] private PresetOption? _selectedPreset;
    [ObservableProperty] private decimal? _minPrice;
    [ObservableProperty] private decimal? _maxPrice;
    [ObservableProperty] private decimal _minDollarVolumeMillions;
    [ObservableProperty] private decimal? _minAtrPercent;
    [ObservableProperty] private decimal? _maxAtrPercent;
    [ObservableProperty] private int _minTradingDays;
    [ObservableProperty] private ScreenerSort _sortBy;
    [ObservableProperty] private int _maxResults;
    [ObservableProperty] private bool _stocksOnly;

    // Results
    [ObservableProperty] private IReadOnlyList<ScreenerRunSummary> _runs = [];
    [ObservableProperty] private ScreenerRunSummary? _selectedRun;
    [ObservableProperty] private IReadOnlyList<ScreenedSymbol> _symbols = [];

    public bool HasSymbols => Symbols.Count > 0;

    /// <summary>E.g. "42 of 5 811 instruments match (data as of 2026-10-30)".</summary>
    public string Summary => SelectedRun is { } run
        ? Loc.Format("Screener_Summary", run.Matched, run.WithData, run.AsOf.ToDateTime(TimeOnly.MinValue))
        : string.Empty;

    partial void OnSymbolsChanged(IReadOnlyList<ScreenedSymbol> value) => OnPropertyChanged(nameof(HasSymbols));

    partial void OnSelectedPresetChanged(PresetOption? value)
    {
        if (value is not null)
            Apply(value.Preset.Criteria);
    }

    partial void OnSelectedRunChanged(ScreenerRunSummary? value)
    {
        OnPropertyChanged(nameof(Summary));
        _ = LoadResultAsync(value, CancellationToken.None);
    }

    protected override Task LoadAsync() => RefreshAsync(CancellationToken.None);

    public async void Receive(ScreenerRunsChangedMessage message) => await RefreshAsync(CancellationToken.None);

    /// <summary>The criteria as entered; the command validates them.</summary>
    public ScreenerCriteria BuildCriteria() => new(
        MinPrice,
        MaxPrice,
        MinDollarVolumeMillions * 1_000_000m,
        MinAtrPercent,
        MaxAtrPercent,
        MinTradingDays,
        Exchanges.Where(e => e.IsSelected).Select(e => e.Code).ToList(),
        SortBy,
        MaxResults,
        StocksOnly);

    [RelayCommand]
    private async Task RunAsync(CancellationToken cancellationToken)
    {
        var result = await _dispatcher.SendAsync(new QueueScreenerCommand(BuildCriteria()), cancellationToken);
        if (result.IsFailure)
            Status.Show(result.Error);
        else
            Status.Show(Loc.Get(result.Value.WasQueued ? "Status_ScreenerQueued" : "Status_ScreenerAlreadyRunning"));
    }

    /// <summary>Lists the stored runs and shows the newest.</summary>
    [RelayCommand]
    private async Task RefreshAsync(CancellationToken cancellationToken)
    {
        var runs = await _dispatcher.SendAsync(new GetScreenerRunsQuery(), cancellationToken);
        if (runs.IsFailure)
        {
            Status.Show(runs.Error);
            return;
        }

        Runs = runs.Value;
        SelectedRun = Runs.FirstOrDefault();
    }

    /// <summary>The selected rows (or the top of the list) on the Compare page, ready to download and backtest.</summary>
    [RelayCommand]
    private void SendToCompare(IList? selectedRows)
    {
        var symbols = SymbolsFor(selectedRows, QueueMarketDataDownloadsCommand.MaxSymbols);
        if (symbols.Count == 0)
            return;

        _messenger.Send(new NavigateMessage(typeof(CompareViewModel)));
        _messenger.Send(new CompareSymbolsMessage(symbols));
    }

    /// <summary>A year of minute data for the selected rows (or the top of the list), as background jobs.</summary>
    [RelayCommand]
    private async Task DownloadDataAsync(IList? selectedRows, CancellationToken cancellationToken)
    {
        var symbols = SymbolsFor(selectedRows, QueueMarketDataDownloadsCommand.MaxSymbols);
        if (symbols.Count == 0)
            return;

        var end = DateOnly.FromDateTime(DateTime.Today);
        var result = await _dispatcher.SendAsync(new QueueMarketDataDownloadsCommand(symbols, Timeframe.Minute, end.AddYears(-1), end), cancellationToken);
        if (result.IsFailure)
            Status.Show(result.Error);
        else if (result.Value.AlreadyQueued.Count == 0)
            Status.Show(Loc.Format("Status_QueuedDownloads", result.Value.JobIds.Count));
        else
            Status.Show(Loc.Format("Status_QueuedDownloadsSkipped", result.Value.JobIds.Count, string.Join(", ", result.Value.AlreadyQueued)));
    }

    private List<string> SymbolsFor(IList? selectedRows, int max)
    {
        var selected = (selectedRows ?? Array.Empty<object>()).OfType<ScreenedSymbol>().ToList();
        return (selected.Count > 0 ? selected : Symbols).OrderBy(s => s.Rank).Take(max).Select(s => s.Symbol).ToList();
    }

    private async Task LoadResultAsync(ScreenerRunSummary? run, CancellationToken cancellationToken)
    {
        if (run is null)
        {
            Symbols = [];
            return;
        }

        var result = await _dispatcher.SendAsync(new GetScreenerResultQuery(run.Id), cancellationToken);
        if (result.IsFailure)
            Status.Show(result.Error);
        else if (SelectedRun?.Id == run.Id)
            Symbols = result.Value.Symbols;
    }

    private void Apply(ScreenerCriteria criteria)
    {
        MinPrice = criteria.MinPrice;
        MaxPrice = criteria.MaxPrice;
        MinDollarVolumeMillions = criteria.MinAverageDollarVolume / 1_000_000m;
        MinAtrPercent = criteria.MinAtrPercent;
        MaxAtrPercent = criteria.MaxAtrPercent;
        MinTradingDays = criteria.MinTradingDays;
        SortBy = criteria.SortBy;
        MaxResults = criteria.MaxResults;
        StocksOnly = criteria.StocksOnly;
        foreach (var exchange in Exchanges)
            exchange.IsSelected = criteria.Exchanges.Contains(exchange.Code, StringComparer.OrdinalIgnoreCase);
    }
}
