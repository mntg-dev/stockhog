using Stockhog.Application.Abstractions.Messaging;
using Stockhog.Application.Abstractions.Results;
using FluentValidation;

namespace Stockhog.Application.Assistant;

/// <summary>Whether the assistant can be used, and with which model.</summary>
public sealed record GetAssistantStatusQuery : IQuery<AssistantStatus>;

public sealed record AssistantStatus(bool IsConfigured, string Model);

/// <summary>Stores the assistant's API key for this Windows user, or removes it when <paramref name="ApiKey"/> is empty.</summary>
public sealed record SaveAssistantApiKeyCommand(string? ApiKey) : ICommand<AssistantStatus>;

internal sealed class SaveAssistantApiKeyCommandValidator : AbstractValidator<SaveAssistantApiKeyCommand>
{
    public SaveAssistantApiKeyCommandValidator() =>
        RuleFor(c => c.ApiKey)
            .Must(key => string.IsNullOrWhiteSpace(key) || key.Trim().Length >= 20 && !key.Trim().Contains(' '))
            .WithMessage("This does not look like an API key.");
}

internal sealed class GetAssistantStatusQueryHandler(IAssistantClient client) : IQueryHandler<GetAssistantStatusQuery, AssistantStatus>
{
    public Task<Result<AssistantStatus>> HandleAsync(GetAssistantStatusQuery query, CancellationToken cancellationToken) =>
        Task.FromResult(Result<AssistantStatus>.Success(new AssistantStatus(client.IsConfigured, client.Model)));
}

internal sealed class SaveAssistantApiKeyCommandHandler(IAssistantCredentials credentials, IAssistantClient client)
    : ICommandHandler<SaveAssistantApiKeyCommand, AssistantStatus>
{
    public Task<Result<AssistantStatus>> HandleAsync(SaveAssistantApiKeyCommand command, CancellationToken cancellationToken)
    {
        credentials.SetApiKey(string.IsNullOrWhiteSpace(command.ApiKey) ? null : command.ApiKey.Trim());
        return Task.FromResult(Result<AssistantStatus>.Success(new AssistantStatus(client.IsConfigured, client.Model)));
    }
}
