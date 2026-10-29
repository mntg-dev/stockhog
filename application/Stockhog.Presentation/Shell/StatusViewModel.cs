using CommunityToolkit.Mvvm.ComponentModel;
using Stockhog.Application.Abstractions.Results;
using Stockhog.Presentation.Localization;

namespace Stockhog.Presentation.Shell;

/// <summary>
/// The shell's status bar, shared by all pages: last message and progress of the current interactive operation.
/// </summary>
public sealed partial class StatusViewModel : ObservableObject
{
    [ObservableProperty] private string _message = Loc.Get("Status_Ready");
    [ObservableProperty] private int _progress;
    [ObservableProperty] private bool _isBusy;

    public void Show(string message) => Message = message;

    public void Show(Error error) => Message = error.Type == ErrorType.Validation
        ? error.Message
        : Loc.Format("Status_Failed", error.Message);
}
