using System.Windows;
using System.Windows.Controls;
using Stockhog.Presentation.Common;
using Stockhog.Presentation.Localization;

namespace Stockhog.App.Hosting;

/// <summary>Modal dialogs owned by the main window; the safe choice ("No", Cancel) is the default.</summary>
internal sealed class MessageBoxConfirmation : IUserConfirmation
{
    public bool Confirm(string title, string message)
    {
        var owner = System.Windows.Application.Current?.MainWindow;
        var result = owner is null
            ? MessageBox.Show(message, title, MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No)
            : MessageBox.Show(owner, message, title, MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
        return result == MessageBoxResult.Yes;
    }

    public string? Prompt(string title, string message)
    {
        var input = new TextBox { Margin = new Thickness(0, 10, 0, 10), MinWidth = 260 };
        var ok = new Button { Content = Loc.Get("Common_Ok"), MinWidth = 80, Margin = new Thickness(0, 0, 8, 0) };
        var cancel = new Button { Content = Loc.Get("Common_Cancel"), MinWidth = 80, IsCancel = true, IsDefault = true };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Children = { ok, cancel } };

        var window = new Window
        {
            Title = title,
            Owner = System.Windows.Application.Current?.MainWindow,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            SizeToContent = SizeToContent.WidthAndHeight,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            Content = new StackPanel
            {
                Margin = new Thickness(16),
                MaxWidth = 420,
                Children = { new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap }, input, buttons }
            }
        };
        ok.Click += (_, _) => window.DialogResult = true;
        window.Loaded += (_, _) => input.Focus();

        return window.ShowDialog() == true ? input.Text : null;
    }
}
