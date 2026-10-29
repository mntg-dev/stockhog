namespace Stockhog.Presentation.Common;

/// <summary>Asks the user to confirm an action; implemented by the host with dialogs.</summary>
public interface IUserConfirmation
{
    bool Confirm(string title, string message);

    /// <summary>Asks the user to type something, e.g. an account number to confirm a dangerous action.</summary>
    /// <returns>The text, or <c>null</c> if the user cancelled.</returns>
    string? Prompt(string title, string message);
}
