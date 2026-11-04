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

    private async void OnTestBrokerKeys(object sender, RoutedEventArgs e)
    {
        if (DataContext is SettingsViewModel viewModel)
            await viewModel.TestBrokerKeysAsync(BrokerKeysInput());
    }

    /// <summary>The secrets are cleared once handed over; a failed save leaves the key ids to correct.</summary>
    private async void OnSaveBrokerKeys(object sender, RoutedEventArgs e)
    {
        if (DataContext is not SettingsViewModel viewModel)
            return;

        var input = BrokerKeysInput();
        BrokerSecretBox.Clear();
        PaperSecretBox.Clear();
        await viewModel.SaveBrokerKeysAsync(input);
    }

    /// <summary>The token is cleared once handed over.</summary>
    private async void OnSaveChatAlerts(object sender, RoutedEventArgs e)
    {
        if (DataContext is not SettingsViewModel viewModel)
            return;

        var token = ChatTokenBox.Password;
        ChatTokenBox.Clear();
        await viewModel.SaveChatAlertsAsync(token);
    }

    private BrokerKeysInput BrokerKeysInput() => new(BrokerKeyBox.Text, BrokerSecretBox.Password, PaperKeyBox.Text, PaperSecretBox.Password);
}
