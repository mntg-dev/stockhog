using System.Buffers;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace Stockhog.Infrastructure.Alpaca.Streaming;

/// <summary>A text-message WebSocket; a seam so the stream services can be tested without a server.</summary>
internal interface IStreamSocket : IAsyncDisposable
{
    Task ConnectAsync(Uri endpoint, CancellationToken cancellationToken);

    Task SendAsync(string message, CancellationToken cancellationToken);

    /// <returns>The next complete message, or <c>null</c> once the server has closed the connection.</returns>
    Task<string?> ReceiveAsync(CancellationToken cancellationToken);
}

internal delegate IStreamSocket StreamSocketFactory();

/// <summary>
/// <see cref="ClientWebSocket"/> allows one send and one receive at a time. Receives happen only on the reader loop;
/// sends can come from any thread (e.g. a watchlist change from the UI), so they are serialized here.
/// </summary>
internal sealed class ClientWebSocketStream : IStreamSocket
{
    private readonly ClientWebSocket _socket = new() { Options = { KeepAliveInterval = TimeSpan.FromSeconds(20) } };
    private readonly SemaphoreSlim _sendLock = new(1, 1);

    public Task ConnectAsync(Uri endpoint, CancellationToken cancellationToken) => _socket.ConnectAsync(endpoint, cancellationToken);

    public async Task SendAsync(string message, CancellationToken cancellationToken)
    {
        await _sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _socket.SendAsync(Encoding.UTF8.GetBytes(message), WebSocketMessageType.Text, endOfMessage: true, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    /// <summary>Joins the frames of one message; Alpaca sends some streams as binary frames holding UTF-8 JSON.</summary>
    public async Task<string?> ReceiveAsync(CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(16 * 1024);
        var message = new ArrayBufferWriter<byte>();
        try
        {
            while (true)
            {
                var result = await _socket.ReceiveAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close)
                    return null;

                message.Write(buffer.AsSpan(0, result.Count));
                if (result.EndOfMessage)
                    return Encoding.UTF8.GetString(message.WrittenSpan);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_socket.State == WebSocketState.Open)
        {
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await _socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, timeout.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is WebSocketException or OperationCanceledException)
            {
                // Closing politely is best effort.
            }
        }

        _socket.Dispose();
        _sendLock.Dispose();
    }
}

internal static class StreamJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    public static string Serialize(object message) => JsonSerializer.Serialize(message, Options);
}
