using Microsoft.Win32;
using Stockhog.Presentation.Common;

namespace Stockhog.App.Hosting;

internal sealed class FileDialogs : IFileDialogs
{
    public string? ChooseSaveFile(string title, string suggestedName, string filter)
    {
        var dialog = new SaveFileDialog
        {
            Title = title,
            FileName = suggestedName,
            Filter = filter,
            OverwritePrompt = true,
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Desktop)
        };
        return dialog.ShowDialog(System.Windows.Application.Current?.MainWindow) == true ? dialog.FileName : null;
    }
}
