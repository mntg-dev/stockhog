using Stockhog.Application.Notifications;
using Stockhog.Domain.Trading;
using Stockhog.Presentation.Localization;

namespace Stockhog.Presentation.Common;

/// <summary>Words a notice in the user's language.</summary>
public static class NoticeText
{
    public static (string Title, string Message) Format(Notice notice)
    {
        var a = notice.Args;
        return notice.Topic switch
        {
            NoticeTopic.OrderFilled => (
                Loc.Get("Notice_OrderFilledTitle"),
                Loc.Format(a[0] is OrderSide.Sell ? "Notice_SoldFilled" : "Notice_BoughtFilled", a[1], a[2], a[3])),
            NoticeTopic.OrderRejected => (Loc.Get("Notice_OrderRejectedTitle"), Loc.Format("Notice_OrderRejected", a[0], a[1])),
            NoticeTopic.HaltedOnDailyLoss => (Loc.Get("Notice_HaltedTitle"), Loc.Format("Notice_HaltedOnDailyLoss", a[0], a[1])),
            NoticeTopic.HaltedOnDrawdown => (Loc.Get("Notice_HaltedTitle"), Loc.Format("Notice_HaltedOnDrawdown", a[0], a[1])),
            NoticeTopic.AutopilotTraded => (Loc.Get("Notice_AutopilotTitle"), Loc.Format("Notice_AutopilotTraded", a[0], a[1])),
            _ => ("Stockhog", notice.Topic.ToString())
        };
    }
}
