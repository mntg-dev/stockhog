using System.Windows;
using System.Windows.Controls;
using Stockhog.Presentation.Assistant;

namespace Stockhog.App.Views;

public partial class AssistantView
{
    private bool _followNewText = true;

    public AssistantView()
    {
        InitializeComponent();
    }

    /// <summary>The page is rebuilt on every visit; refresh whether a key exists, e.g. after one was entered in Settings.</summary>
    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (DataContext is AssistantViewModel viewModel)
            await viewModel.RefreshStatusAsync();
    }

    /// <summary>Keeps the newest text in view while the answer streams in, unless the user scrolled up to read.</summary>
    private void OnScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (e.ExtentHeightChange == 0)
            _followNewText = Scroller.VerticalOffset >= Scroller.ScrollableHeight - 2;
        else if (_followNewText)
            Scroller.ScrollToEnd();
    }
}
