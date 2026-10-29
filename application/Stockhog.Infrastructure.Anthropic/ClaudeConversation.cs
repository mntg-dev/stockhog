using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;
using Anthropic;
using Anthropic.Models.Beta.Messages;
using Stockhog.Application.Assistant;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Stockhog.Infrastructure.Anthropic;

/// <summary>
/// One chat with Claude: streams each answer, runs the app tools Claude asks for, and loops until the answer is
/// complete. The history only grows (each response is appended unchanged, thinking blocks included), which the API
/// needs for the model's thinking to stay valid across turns. A question that fails is removed again, so the
/// conversation can continue.
/// </summary>
/// <param name="createClient">Creates the API client for a key; tests point it at a stub server.</param>
internal sealed class ClaudeConversation(
    IAssistantCredentials credentials,
    AnthropicOptions options,
    IServiceScopeFactory scopeFactory,
    Func<string, AnthropicClient> createClient,
    ILogger<ClaudeConversation> logger) : IAssistantConversation
{
    /// <summary>When the model declines a request for policy reasons, the API retries it on a fallback model.</summary>
    private const string ServerSideFallbackBeta = "server-side-fallback-2026-07-01";

    private readonly List<BetaMessageParam> _history = [];
    private readonly SemaphoreSlim _oneAtATime = new(1, 1);

    public async IAsyncEnumerable<AssistantUpdate> SendAsync(string message, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var apiKey = credentials.GetApiKey() ?? throw new AssistantException("No API key: enter your Anthropic API key in Settings.");
        if (!await _oneAtATime.WaitAsync(0, cancellationToken))
            throw new AssistantException("Wait for the current answer to finish.");

        // C# cannot yield inside try/catch, so the work runs in a task and hands updates over through a channel.
        var updates = Channel.CreateUnbounded<AssistantUpdate>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
        var work = Task.Run(() => AnswerAsync(apiKey, message, updates.Writer, cancellationToken), CancellationToken.None);

        try
        {
            await foreach (var update in updates.Reader.ReadAllAsync(cancellationToken))
                yield return update;

            await work;
        }
        finally
        {
            _oneAtATime.Release();
        }
    }

    private async Task AnswerAsync(string apiKey, string message, ChannelWriter<AssistantUpdate> updates, CancellationToken cancellationToken)
    {
        var historyBefore = _history.Count;
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var toolbox = scope.ServiceProvider.GetRequiredService<IAssistantToolbox>();
            var client = createClient(apiKey);
            var tools = toolbox.Tools.Select(ToClaudeTool).ToList();

            _history.Add(new BetaUserMessageParam { Content = message });

            for (var turn = 0; turn < options.MaxTurnsPerQuestion; turn++)
            {
                var response = await StreamTurnAsync(client, tools, updates, cancellationToken);

                if (response.StopReason == "refusal")
                {
                    Rollback(historyBefore);
                    updates.TryWrite(new AssistantTextDelta("\n\n(The model declined to answer this request.)"));
                    break;
                }

                if (response.StopReason == "max_tokens")
                {
                    Rollback(historyBefore);
                    throw new AssistantException("The answer was too long and was cut off; ask a narrower question.");
                }

                _history.Add(new BetaAssistantMessageParam { Content = ToParams(response.Content) });
                if (response.StopReason != "tool_use")
                    break;

                _history.Add(new BetaUserMessageParam { Content = await RunToolsAsync(toolbox, response, cancellationToken) });
            }

            updates.TryWrite(new AssistantCompleted());
            updates.Complete();
        }
        catch (Exception ex)
        {
            Rollback(historyBefore);
            updates.Complete(ex is OperationCanceledException or AssistantException ? ex : Describe(ex));
        }
    }

    /// <summary>Sends the conversation, forwarding text and tool calls as they arrive, and returns the complete response.</summary>
    private async Task<BetaMessage> StreamTurnAsync(AnthropicClient client, List<BetaToolUnion> tools, ChannelWriter<AssistantUpdate> updates, CancellationToken cancellationToken)
    {
        var parameters = new MessageCreateParams
        {
            Model = options.Model,
            MaxTokens = options.MaxTokens,
            System = AssistantInstructions.System,
            Messages = _history.ToList(),
            Tools = tools,
            OutputConfig = new BetaOutputConfig { Effort = options.Effort },
            // Caches the tools, instructions and history so far; each turn re-reads them at a fraction of the price.
            CacheControl = new BetaCacheControlEphemeral(),
            Betas = [ServerSideFallbackBeta],
            Fallbacks = new Default()
        };

        // Text is forwarded as it arrives; the events are kept to assemble the complete message afterwards.
        var events = new List<BetaRawMessageStreamEvent>();
        await foreach (var streamEvent in client.Beta.Messages.CreateStreaming(parameters, cancellationToken))
        {
            events.Add(streamEvent);

            if (streamEvent.TryPickContentBlockDelta(out var delta) && delta.Delta.TryPickText(out var text))
                updates.TryWrite(new AssistantTextDelta(text.Text));
            else if (streamEvent.TryPickContentBlockStart(out var start) && start.ContentBlock.TryPickBetaToolUse(out var toolUse))
                updates.TryWrite(new AssistantToolUse(toolUse.Name));
        }

        return await events.ToAsyncEnumerable().Aggregate();
    }

    /// <summary>Runs every tool call of the response at once and returns all results in one message, as the API expects.</summary>
    private async Task<List<BetaContentBlockParam>> RunToolsAsync(IAssistantToolbox toolbox, BetaMessage response, CancellationToken cancellationToken)
    {
        var calls = response.Content
            .Select(block => block.TryPickToolUse(out var toolUse) ? toolUse : null)
            .OfType<BetaToolUseBlock>()
            .ToList();

        var results = await Task.WhenAll(calls.Select(async call =>
        {
            var input = JsonSerializer.SerializeToElement(call.Input);
            logger.LogInformation("Assistant tool {Tool} {Input}", call.Name, input.GetRawText());
            var result = await toolbox.ExecuteAsync(call.Name, input, cancellationToken);
            return (BetaContentBlockParam)new BetaToolResultBlockParam { ToolUseID = call.ID, Content = result.Content, IsError = result.IsError };
        }));

        return results.ToList();
    }

    /// <summary>
    /// The response blocks as request blocks, unchanged: thinking keeps its signature, which the API checks. Block
    /// kinds this app does not handle itself (e.g. a fallback notice) are passed back as their raw JSON.
    /// </summary>
    private static List<BetaContentBlockParam> ToParams(IReadOnlyList<BetaContentBlock> content) =>
        content.Select(block =>
        {
            if (block.TryPickText(out var text))
                return (BetaContentBlockParam)new BetaTextBlockParam { Text = text.Text };
            if (block.TryPickThinking(out var thinking))
                return new BetaThinkingBlockParam { Thinking = thinking.Thinking, Signature = thinking.Signature };
            if (block.TryPickRedactedThinking(out var redacted))
                return new BetaRedactedThinkingBlockParam { Data = redacted.Data };
            if (block.TryPickToolUse(out var toolUse))
                return new BetaToolUseBlockParam { ID = toolUse.ID, Name = toolUse.Name, Input = toolUse.Input };
            return new BetaContentBlockParam(block.Json);
        }).ToList();

    /// <summary>
    /// The app's tool as a Claude tool. Inputs stream as they are written; the toolbox validates every input itself.
    /// </summary>
    private static BetaToolUnion ToClaudeTool(AssistantTool tool)
    {
        var properties = tool.InputSchema["properties"]?.AsObject()
            .ToDictionary(p => p.Key, p => JsonSerializer.SerializeToElement(p.Value)) ?? [];
        var required = tool.InputSchema["required"]?.AsArray().Select(r => r!.GetValue<string>()).ToList() ?? [];

        return new BetaTool
        {
            Name = tool.Name,
            Description = tool.Description,
            InputSchema = new InputSchema { Properties = properties, Required = required },
            EagerInputStreaming = true
        };
    }

    private void Rollback(int length)
    {
        if (_history.Count > length)
            _history.RemoveRange(length, _history.Count - length);
    }

    private AssistantException Describe(Exception ex)
    {
        logger.LogWarning(ex, "Assistant request failed");
        return new AssistantException($"The assistant could not answer: {ex.Message}", ex);
    }
}
