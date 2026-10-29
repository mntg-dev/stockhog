using Stockhog.Application.Assistant;

namespace Stockhog.Application.Tests.TestSupport;

internal sealed class InMemoryAssistantCredentials : IAssistantCredentials
{
    public string? ApiKey { get; set; }

    public bool HasApiKey => ApiKey is not null;

    public string? GetApiKey() => ApiKey;

    public void SetApiKey(string? apiKey) => ApiKey = apiKey;
}

/// <summary>Reports the credentials' state; conversations are not part of the Application tests.</summary>
internal sealed class FakeAssistantClient(InMemoryAssistantCredentials credentials) : IAssistantClient
{
    public bool IsConfigured => credentials.HasApiKey;

    public string Model => "test-model";

    public IAssistantConversation StartConversation() => throw new NotSupportedException();
}

internal sealed class RecordingAssistantActions : IAssistantActions
{
    public List<OrderProposal> Proposals { get; } = [];

    public void ProposeOrder(OrderProposal proposal) => Proposals.Add(proposal);
}
