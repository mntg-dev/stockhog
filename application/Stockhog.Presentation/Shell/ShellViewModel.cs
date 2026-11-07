using System.ComponentModel;
using System.Windows.Data;
using System.Windows.Markup;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Stockhog.Presentation.Jobs;
using Stockhog.Presentation.Localization;

namespace Stockhog.Presentation.Shell;

/// <summary>
/// Main window: sidebar navigation between page view models (each rendered through a DataTemplate), plus the shared
/// status bar and the number of active jobs.
/// </summary>
public sealed partial class ShellViewModel : ObservableObject, IRecipient<NavigateMessage>
{
    public ShellViewModel(
        IEnumerable<PageEntry> entries,
        IServiceProvider services,
        JobsViewModel jobs,
        StatusViewModel status,
        UpdateBannerViewModel update,
        IMessenger messenger)
    {
        Status = status;
        Update = update;
        Jobs = jobs;

        var ordered = entries.OrderBy(e => e.Order).ToList();
        var items = ordered.ToDictionary(
            e => e,
            e => new NavigationItem(e.TitleKey, e.Glyph, (PageViewModel)services.GetRequiredService(e.PageType), e.GroupKey, e.Feature));
        Pages = ordered.Select(e => items[e]).ToList();
        Pipeline = ordered
            .Where(e => e.PipelineKey is not null)
            .Select((e, index) => new PipelineStep(e.PipelineKey!, items[e], index > 0 && e.PipelineArrow))
            .ToList();

        // Grouped for the sidebar's section headers; the list itself stays in menu order.
        PagesView = new ListCollectionView(Pages.ToList());
        PagesView.GroupDescriptions.Add(new PropertyGroupDescription(nameof(NavigationItem.GroupKey)));

        messenger.RegisterAll(this);
        Loc.Instance.PropertyChanged += OnLanguageChanged;
        SelectedItem = Pages[0];
    }

    public IReadOnlyList<NavigationItem> Pages { get; }

    public ListCollectionView PagesView { get; }

    /// <summary>From the market to a trade; shown above the pages that take part.</summary>
    public IReadOnlyList<PipelineStep> Pipeline { get; }

    public bool IsOnPipeline => Pipeline.Any(s => s.IsCurrent);

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
        foreach (var step in Pipeline)
            step.IsCurrent = step.Item == value;
        OnPropertyChanged(nameof(IsOnPipeline));

        if (value is not null)
            _ = value.Page.OnNavigatedToAsync();
    }

    [RelayCommand]
    private void OpenPipelineStep(PipelineStep step) => SelectedItem = step.Item;

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
