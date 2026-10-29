using Anthropic;
using Stockhog.Application.Assistant;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Stockhog.Infrastructure.Anthropic;

/// <summary>The assistant backed by Claude. Each conversation gets a client with the API key current at that time.</summary>
internal sealed class ClaudeAssistantClient(
    IAssistantCredentials credentials,
    IOptions<AnthropicOptions> options,
    IServiceScopeFactory scopeFactory,
    ILoggerFactory loggerFactory) : IAssistantClient
{
    public bool IsConfigured => credentials.HasApiKey;

    public string Model => options.Value.Model;

    public IAssistantConversation StartConversation() =>
        new ClaudeConversation(credentials, options.Value, scopeFactory, apiKey => new AnthropicClient { ApiKey = apiKey }, loggerFactory.CreateLogger<ClaudeConversation>());
}
