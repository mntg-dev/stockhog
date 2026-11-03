using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Stockhog.Application.Abstractions.Messaging;
using Stockhog.Application.Updates;
using Stockhog.Presentation.Common;
using Stockhog.Presentation.Localization;
using Stockhog.Presentation.Settings;

namespace Stockhog.Presentation.Shell;

/// <summary>A banner above the pages when a newer release is published; checked once per start.</summary>
public sealed partial class UpdateBannerViewModel(IDispatcher dispatcher, IExternalLinks links, AppEnvironmentInfo environment) : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsVisible), nameof(Text))]
    private UpdateCheck _update = UpdateCheck.None;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsVisible))]
    private bool _dismissed;

    public bool IsVisible => Update.IsAvailable && !Dismissed;

    public string Text => Update.LatestVersion is { } latest ? Loc.Format("Update_Available", latest, environment.Version) : "";

    public async Task CheckAsync()
    {
        var check = await dispatcher.SendAsync(new CheckForUpdateQuery(environment.Version));
        if (check.IsSuccess)
            Update = check.Value;
    }

    [RelayCommand]
    private void Download()
    {
        if (Update.InstallerUrl is { } url)
            links.Open(url);
    }

    [RelayCommand]
    private void Dismiss() => Dismissed = true;
}
