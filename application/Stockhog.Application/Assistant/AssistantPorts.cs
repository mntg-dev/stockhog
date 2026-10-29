using System.Text.Json;
using System.Text.Json.Nodes;
using Stockhog.Domain.Trading;

namespace Stockhog.Application.Assistant;

/// <summary>The language model behind the assistant (an adapter, e.g. Claude).</summary>
public interface IAssistantClient
{
    /// <summary><c>false</c> until an API key is available.</summary>
    bool IsConfigured { get; }

    /// <summary>The model name, shown in the UI.</summary>
    string Model { get; }

    /// <summary>A new chat; it keeps its own history until it is dropped.</summary>
    IAssistantConversation StartConversation();
}

/// <summary>
/// One chat with the assistant. Messages must be sent one at a time: wait for the stream of a message to end before
/// sending the next.
/// </summary>
public interface IAssistantConversation
{
    /// <summary>Streams the answer as it is written, including which tools the assistant uses on the way.</summary>
    /// <exception cref="AssistantException">The model could not be reached or refused the request.</exception>
    IAsyncEnumerable<AssistantUpdate> SendAsync(string message, CancellationToken cancellationToken = default);
}

public abstract record AssistantUpdate;

/// <summary>Another piece of the answer text.</summary>
public sealed record AssistantTextDelta(string Text) : AssistantUpdate;

/// <summary>The assistant is calling one of the app's tools, e.g. to read the ranking.</summary>
public sealed record AssistantToolUse(string ToolName) : AssistantUpdate;

/// <summary>The answer is complete.</summary>
public sealed record AssistantCompleted : AssistantUpdate;

public sealed class AssistantException(string message, Exception? innerException = null) : Exception(message, innerException);

/// <summary>Where the assistant's API key is kept.</summary>
public interface IAssistantCredentials
{
    bool HasApiKey { get; }

    string? GetApiKey();

    /// <param name="apiKey">The new key, or <c>null</c> to remove the stored one.</param>
    void SetApiKey(string? apiKey);
}

/// <summary>
/// What the assistant may do in the UI. It never places orders itself: a proposed order only fills in the order
/// ticket, which the user reviews and submits through the usual risk checks and confirmation.
/// </summary>
public interface IAssistantActions
{
    void ProposeOrder(OrderProposal proposal);
}

/// <param name="Reason">Why the assistant proposes it, shown next to the ticket.</param>
public sealed record OrderProposal(
    string Symbol,
    OrderSide Side,
    OrderType Type,
    decimal Quantity,
    decimal? LimitPrice,
    string Reason);

/// <summary>A tool the assistant can call: a name, what it does, and a JSON schema of its input.</summary>
public sealed record AssistantTool(string Name, string Description, JsonObject InputSchema);

/// <summary>The app functions offered to the assistant. Results are JSON text for the model to read.</summary>
public interface IAssistantToolbox
{
    IReadOnlyList<AssistantTool> Tools { get; }

    /// <returns>The result, or an error description the model can react to; never throws for bad input.</returns>
    Task<AssistantToolResult> ExecuteAsync(string toolName, JsonElement input, CancellationToken cancellationToken = default);
}

public sealed record AssistantToolResult(string Content, bool IsError = false);
