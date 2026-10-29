using System.Windows;
using Stockhog.Presentation.Shell;

namespace Stockhog.App;

public partial class MainWindow : Window
{
    public MainWindow(ShellViewModel shell)
    {
        InitializeComponent();
        DataContext = shell;
        Loaded += async (_, _) =>
        {
            if (shell.CurrentPage is { } page)
                await page.OnNavigatedToAsync();
        };
    }
}
