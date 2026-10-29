using Stockhog.Application.Assistant;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Stockhog.Infrastructure.Anthropic;

public static class AnthropicDependencyInjection
{
    /// <summary>
    /// Claude as the <see cref="IAssistantClient"/>, with the API key stored encrypted for the Windows user.
    /// The host must register an <see cref="IAssistantActions"/> (the UI).
    /// </summary>
    public static IServiceCollection AddAnthropicAssistant(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<AnthropicOptions>()
            .Bind(configuration.GetSection(AnthropicOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddSingleton<IAssistantCredentials>(sp =>
            new ProtectedApiKeyStore(sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<AnthropicOptions>>()));
        services.AddSingleton<IAssistantClient, ClaudeAssistantClient>();
        return services;
    }
}
