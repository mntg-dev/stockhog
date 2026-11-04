namespace Stockhog.Presentation.Common;

/// <summary>File choosers, implemented by the host.</summary>
public interface IFileDialogs
{
    /// <param name="filter">In the Windows dialog format, e.g. <c>Zip files|*.zip</c>.</param>
    /// <returns>The chosen path, or null if the user cancelled.</returns>
    string? ChooseSaveFile(string title, string suggestedName, string filter);
}
