using CommunityToolkit.Mvvm.ComponentModel;

namespace Stockhog.Presentation.Shell;

/// <summary>
/// One stop on the way from the whole market to a trade, shown in a strip above the pages that take part, so a user
/// always sees where they are and what comes next.
/// </summary>
/// <param name="TitleKey">Resource key of the step's label.</param>
/// <param name="Item">The menu entry of the step's page.</param>
/// <param name="ShowsArrow">False for the first step.</param>
public sealed partial class PipelineStep(string titleKey, NavigationItem item, bool showsArrow) : ObservableObject
{
    public string TitleKey { get; } = titleKey;

    public NavigationItem Item { get; } = item;

    public bool ShowsArrow { get; } = showsArrow;

    [ObservableProperty] private bool _isCurrent;
}
