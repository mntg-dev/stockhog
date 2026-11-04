using Stockhog.Application.Notifications;
using Stockhog.Domain.Trading;
using Stockhog.Presentation.Common;
using Stockhog.Presentation.Localization;

namespace Stockhog.Presentation.Tests;

[Collection(nameof(CultureCollection))]
public sealed class NoticeTextTests : IDisposable
{
    private readonly System.Globalization.CultureInfo _before = Loc.Instance.Culture;

    public NoticeTextTests() => Loc.Instance.SetCulture(Loc.English);

    public void Dispose() => Loc.Instance.SetCulture(_before);

    [Fact]
    public void Every_topic_has_its_own_wording()
    {
        var notices = new[]
        {
            new Notice(NoticeTopic.OrderFilled, [OrderSide.Buy, 10m, "AAPL", 101.5m]),
            new Notice(NoticeTopic.OrderRejected, ["AAPL", "insufficient buying power"]),
            new Notice(NoticeTopic.HaltedOnDailyLoss, [2_000m, 1_000m]),
            new Notice(NoticeTopic.HaltedOnDrawdown, [16m, 15m]),
            new Notice(NoticeTopic.AutopilotTraded, [2, 1])
        };

        var texts = notices.Select(NoticeText.Format).ToList();

        Assert.Equal(Enum.GetValues<NoticeTopic>().Length, notices.Length);
        Assert.All(texts, t => Assert.DoesNotContain("[", t.Title + t.Message));
        Assert.Equal("Bought 10 AAPL at 101.50.", texts[0].Message);
        Assert.Contains("2,000.00 USD", texts[2].Message);
    }

    [Fact]
    public void A_sale_reads_as_sold() =>
        Assert.StartsWith("Sold 5 SPY", NoticeText.Format(new Notice(NoticeTopic.OrderFilled, [OrderSide.Sell, 5m, "SPY", 700m])).Message);
}
