using System.ComponentModel.DataAnnotations;

namespace Stockhog.Infrastructure.Anthropic;

/// <summary>
/// Settings of the assistant. The API key is normally entered on the Settings page and stored encrypted for the
/// Windows user; <see cref="ApiKey"/> (or the <c>ANTHROPIC_API_KEY</c> environment variable) is a fallback.
/// </summary>
public sealed class AnthropicOptions
{
    public const string SectionName = "Anthropic";

    public string? ApiKey { get; set; }

    [Required]
    public string Model { get; set; } = "claude-opus-5-5";

    /// <summary>How much the model thinks before answering: low, medium, high, xhigh or max.</summary>
    [Required, RegularExpression("^(low|medium|high|xhigh|max)$")]
    public string Effort { get; set; } = "medium";

    [Range(1024, 128_000)]
    public int MaxTokens { get; set; } = 32_000;

    /// <summary>Requests to the model per question, tool calls included; stops a runaway loop.</summary>
    [Range(1, 50)]
    public int MaxTurnsPerQuestion { get; set; } = 12;
}
