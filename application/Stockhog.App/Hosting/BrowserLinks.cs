using System.Diagnostics;
using Stockhog.Presentation.Common;

namespace Stockhog.App.Hosting;

/// <summary>Hands HTTPS links to the default browser; never starts a program or opens a local path.</summary>
internal sealed class BrowserLinks : IExternalLinks
{
    public void Open(Uri url)
    {
        if (!url.IsAbsoluteUri || url.Scheme != Uri.UriSchemeHttps)
            return;

        using var _ = Process.Start(new ProcessStartInfo(url.AbsoluteUri) { UseShellExecute = true });
    }
}
