using Stockhog.Application.Notifications;
using Stockhog.Presentation.Common;
using Microsoft.Extensions.Logging;

namespace Stockhog.App.Hosting;

/// <summary>Sends notices to the chat set up on Settings, in the background; does nothing until one is set up.</summary>
internal sealed class ChatNotifier(IChatChannel channel, ILogger<ChatNotifier> logger) : INotifier
{
    public void Notify(Notice notice)
    {
        if (channel.Target is not { } target)
            return;

        var (title, message) = NoticeText.Format(notice);
        _ = Task.Run(async () =>
        {
            try
            {
                await channel.SendAsync(target, $"{title}\n{message}");
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Could not send the notice {Topic} to the chat", notice.Topic);
            }
        });
    }
}

/// <summary>Every notice goes to every channel; one failing channel does not stop the others.</summary>
internal sealed class CompositeNotifier(IReadOnlyList<INotifier> notifiers, ILogger<CompositeNotifier> logger) : INotifier
{
    public void Notify(Notice notice)
    {
        foreach (var notifier in notifiers)
        {
            try
            {
                notifier.Notify(notice);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "A notifier failed on {Topic}", notice.Topic);
            }
        }
    }
}
