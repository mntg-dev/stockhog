using Stockhog.Application.Abstractions.Messaging;
using Stockhog.Application.Abstractions.Results;
using FluentValidation;

namespace Stockhog.Application.Notifications;

/// <summary>Where chat alerts go: a Telegram bot and the chat it writes to.</summary>
/// <param name="BotToken">From BotFather; a secret, never shown or logged.</param>
public sealed record ChatTarget(string BotToken, string ChatId)
{
    public override string ToString() => $"ChatTarget(chat {ChatId})";
}

/// <summary>Sends notices as chat messages, e.g. to the user's phone.</summary>
public interface IChatChannel
{
    bool IsConfigured { get; }

    /// <returns>Null when sent, else why not.</returns>
    Task<string?> SendAsync(ChatTarget target, string text, CancellationToken cancellationToken = default);

    /// <summary>The stored target, or null.</summary>
    ChatTarget? Target { get; }

    /// <summary>Stores the target encrypted for this Windows user, or removes it when null.</summary>
    void Save(ChatTarget? target);
}

public sealed record ChatAlertStatus(bool IsConfigured, string? ChatId);

public sealed record GetChatAlertStatusQuery : IQuery<ChatAlertStatus>;

/// <summary>Sends a test message first and stores the target only if it arrived.</summary>
public sealed record SaveChatAlertsCommand(string BotToken, string ChatId, string TestMessage) : ICommand<ChatAlertStatus>
{
    public override string ToString() => $"{nameof(SaveChatAlertsCommand)}(chat {ChatId})";
}

public sealed record RemoveChatAlertsCommand : ICommand<ChatAlertStatus>;

internal sealed class SaveChatAlertsCommandValidator : AbstractValidator<SaveChatAlertsCommand>
{
    public SaveChatAlertsCommandValidator()
    {
        // Telegram bot tokens look like 123456789:AA...; chat ids are numbers, negative for groups.
        RuleFor(c => c.BotToken).Matches(@"^\s*\d{5,}:[A-Za-z0-9_-]{30,}\s*$").WithMessage("This does not look like a Telegram bot token.");
        RuleFor(c => c.ChatId).Matches(@"^\s*-?\d{3,20}\s*$").WithMessage("The chat id is a number, e.g. 123456789.");
        RuleFor(c => c.TestMessage).NotEmpty();
    }
}

internal sealed class GetChatAlertStatusQueryHandler(IChatChannel channel) : IQueryHandler<GetChatAlertStatusQuery, ChatAlertStatus>
{
    public Task<Result<ChatAlertStatus>> HandleAsync(GetChatAlertStatusQuery query, CancellationToken cancellationToken) =>
        Task.FromResult(Result<ChatAlertStatus>.Success(new ChatAlertStatus(channel.IsConfigured, channel.Target?.ChatId)));
}

internal sealed class SaveChatAlertsCommandHandler(IChatChannel channel) : ICommandHandler<SaveChatAlertsCommand, ChatAlertStatus>
{
    public async Task<Result<ChatAlertStatus>> HandleAsync(SaveChatAlertsCommand command, CancellationToken cancellationToken)
    {
        var target = new ChatTarget(command.BotToken.Trim(), command.ChatId.Trim());
        if (await channel.SendAsync(target, command.TestMessage, cancellationToken) is { } problem)
            return Error.Validation(["The test message was not delivered: " + problem]);

        channel.Save(target);
        return new ChatAlertStatus(true, target.ChatId);
    }
}

internal sealed class RemoveChatAlertsCommandHandler(IChatChannel channel) : ICommandHandler<RemoveChatAlertsCommand, ChatAlertStatus>
{
    public Task<Result<ChatAlertStatus>> HandleAsync(RemoveChatAlertsCommand command, CancellationToken cancellationToken)
    {
        channel.Save(null);
        return Task.FromResult(Result<ChatAlertStatus>.Success(new ChatAlertStatus(false, null)));
    }
}
