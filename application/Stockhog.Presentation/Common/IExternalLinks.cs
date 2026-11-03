namespace Stockhog.Presentation.Common;

/// <summary>Opens a web link in the user's browser. Implemented by the host.</summary>
public interface IExternalLinks
{
    /// <summary>Only HTTPS links are opened; anything else is ignored.</summary>
    void Open(Uri url);
}
