using System.Drawing;
using System.Windows.Forms;
using Stockhog.Application.Notifications;
using Stockhog.Presentation.Common;
using Stockhog.Presentation.Settings;
using Microsoft.Extensions.Logging;

namespace Stockhog.App.Hosting;

/// <summary>
/// Shows notices as Windows notifications from a tray icon, unless the user turned them off on Settings. Called from
/// any thread; the icon lives on the UI thread. Failures are logged, never thrown.
/// </summary>
internal sealed class WindowsNotifier(IUserSettingsStore settings, ILogger<WindowsNotifier> logger) : INotifier, IDisposable
{
    private const int ShowForMs = 8_000;

    private NotifyIcon? _icon;

    public void Notify(Notice notice)
    {
        logger.LogInformation("Notice {Topic}: {Args}", notice.Topic, string.Join(", ", notice.Args));
        try
        {
            if (!settings.Load().ShowNotifications)
                return;

            var (title, message) = NoticeText.Format(notice);
            System.Windows.Application.Current?.Dispatcher.BeginInvoke(() => Show(title, message, notice.IsWarning));
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not show the notice {Topic}", notice.Topic);
        }
    }

    private void Show(string title, string message, bool warning)
    {
        try
        {
            _icon ??= new NotifyIcon
            {
                Icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath!) ?? SystemIcons.Application,
                Text = "Stockhog",
                Visible = true
            };
            _icon.ShowBalloonTip(ShowForMs, title, message, warning ? ToolTipIcon.Warning : ToolTipIcon.Info);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not show a Windows notification");
        }
    }

    /// <summary>Removes the tray icon at exit, so no orphan icon stays behind.</summary>
    public void Dispose()
    {
        if (_icon is null)
            return;
        _icon.Visible = false;
        _icon.Dispose();
    }
}
