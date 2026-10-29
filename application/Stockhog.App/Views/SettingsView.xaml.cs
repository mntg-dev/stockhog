using System.Windows;
using Stockhog.Presentation.Settings;

namespace Stockhog.App.Views;

public partial class SettingsView
{
    public SettingsView()
    {
        InitializeComponent();
    }

    /// <summary>Passes the key to the view model and clears the box, so the key does not stay in the UI.</summary>
    private void OnSaveApiKey(object sender, RoutedEventArgs e)
    {
        if (DataContext is SettingsViewModel viewModel)
            viewModel.SaveApiKeyCommand.Execute(ApiKeyBox.Password);

        ApiKeyBox.Clear();
    }
}
