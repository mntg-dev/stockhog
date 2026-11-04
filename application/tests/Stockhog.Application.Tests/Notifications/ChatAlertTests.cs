using Stockhog.Application.Notifications;
using Stockhog.Application.Tests.TestSupport;

namespace Stockhog.Application.Tests.Notifications;

public class ChatAlertTests
{
    private const string Token = "123456789:AAEhBOweik6ad9r_QXMENQjcrGbqCr4K-ab";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_test_message_is_sent_before_the_chat_is_saved()
    {
        await using var host = new ApplicationTestHost();

        var saved = await host.Dispatcher.SendAsync(new SaveChatAlertsCommand($" {Token} ", " 987654321 ", "hello"), Ct);

        Assert.True(saved.IsSuccess, saved.Error?.Message);
        Assert.Equal(new ChatAlertStatus(true, "987654321"), saved.Value);
        Assert.Equal((new ChatTarget(Token, "987654321"), "hello"), Assert.Single(host.Chat.Sent));
        Assert.Equal(new ChatTarget(Token, "987654321"), host.Chat.Target);
    }

    [Fact]
    public async Task A_chat_that_does_not_receive_the_test_is_not_saved()
    {
        await using var host = new ApplicationTestHost();
        host.Chat.Refusal = "Bad Request: chat not found";

        var saved = await host.Dispatcher.SendAsync(new SaveChatAlertsCommand(Token, "987654321", "hello"), Ct);

        Assert.Contains("chat not found", saved.Error?.Message);
        Assert.Null(host.Chat.Target);
    }

    [Theory]
    [InlineData("not-a-token", "987654321")]
    [InlineData(Token, "@my_channel")]
    public async Task Inputs_that_cannot_be_right_are_refused_before_sending(string token, string chatId)
    {
        await using var host = new ApplicationTestHost();

        var saved = await host.Dispatcher.SendAsync(new SaveChatAlertsCommand(token, chatId, "hello"), Ct);

        Assert.True(saved.IsFailure);
        Assert.Empty(host.Chat.Sent);
    }

    [Fact]
    public void The_target_never_prints_its_token() =>
        Assert.DoesNotContain(Token, new ChatTarget(Token, "1").ToString() + new SaveChatAlertsCommand(Token, "1", "x"));
}
