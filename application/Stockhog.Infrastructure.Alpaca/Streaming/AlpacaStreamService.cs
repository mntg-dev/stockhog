using System.Collections.Concurrent;
using Stockhog.Application.Trading;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Stockhog.Infrastructure.Alpaca.Streaming;

/// <summary>
/// A long-lived WebSocket connection: connect, authenticate, read messages until the connection drops, then reconnect
/// with exponential backoff (1 s doubling up to <see cref="MaxBackoff"/>, reset after a successful handshake).
/// </summary>
internal abstract class AlpacaStreamService(
    StreamSocketFactory socketFactory,
    ConnectionMonitor monitor,
    TimeProvider clock,
    ILogger logger) : BackgroundService
{
    internal static readonly TimeSpan InitialBackoff = TimeSpan.FromSeconds(1);
    internal static readonly TimeSpan MaxBackoff = TimeSpan.FromMinutes(1);

    /// <summary>Wrong keys will not fix themselves: wait longer before trying again.</summary>
    internal static readonly TimeSpan AuthenticationFailureBackoff = TimeSpan.FromMinutes(5);

    private IStreamSocket? _socket;

    protected abstract string Name { get; }

    protected abstract Uri Endpoint { get; }

    protected abstract bool HasCredentials { get; }

    /// <summary>The socket of the current connection, or <c>null</c> while disconnected.</summary>
    protected IStreamSocket? CurrentSocket => Volatile.Read(ref _socket);

    /// <summary>Authenticates and subscribes; throw <see cref="StreamAuthenticationException"/> for refused keys.</summary>
    protected abstract Task HandshakeAsync(IStreamSocket socket, CancellationToken cancellationToken);

    /// <summary>Called once the handshake succeeded, e.g. to catch up on what was missed while disconnected.</summary>
    protected virtual Task OnConnectedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    protected abstract Task OnMessageAsync(string message, CancellationToken cancellationToken);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!HasCredentials)
        {
            monitor.Report(Name, ConnectionState.NotConfigured, "Alpaca keys are not configured.");
            logger.LogInformation("{Stream} stream not started: Alpaca keys are not configured", Name);
            return;
        }

        var backoff = InitialBackoff;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                monitor.Report(Name, ConnectionState.Connecting, null);
                await using var socket = socketFactory();
                await socket.ConnectAsync(Endpoint, stoppingToken);
                await HandshakeAsync(socket, stoppingToken);

                Volatile.Write(ref _socket, socket);
                monitor.Report(Name, ConnectionState.Connected, null);
                logger.LogInformation("{Stream} stream connected", Name);
                backoff = InitialBackoff;
                await OnConnectedAsync(stoppingToken);

                while (await socket.ReceiveAsync(stoppingToken) is { } message)
                    await OnMessageAsync(message, stoppingToken);

                logger.LogWarning("{Stream} stream closed by the server", Name);
                monitor.Report(Name, ConnectionState.Reconnecting, "Closed by the server.");
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (StreamAuthenticationException ex)
            {
                logger.LogError("{Stream} stream: authentication refused: {Reason}", Name, ex.Message);
                monitor.Report(Name, ConnectionState.Reconnecting, ex.Message);
                backoff = AuthenticationFailureBackoff;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "{Stream} stream failed; reconnecting in {Backoff}", Name, backoff);
                monitor.Report(Name, ConnectionState.Reconnecting, ex.Message);
            }
            finally
            {
                Volatile.Write(ref _socket, null);
            }

            try
            {
                await Task.Delay(backoff, clock, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            backoff = TimeSpan.FromTicks(Math.Min(backoff.Ticks * 2, Math.Max(MaxBackoff.Ticks, backoff.Ticks)));
        }

        monitor.Report(Name, ConnectionState.Stopped, null);
    }
}

internal sealed class StreamAuthenticationException(string message) : Exception(message);

/// <summary>Keeps the latest state of each stream for the UI.</summary>
internal sealed class ConnectionMonitor(TimeProvider clock) : IConnectionMonitor
{
    private readonly ConcurrentDictionary<string, ConnectionStatus> _statuses = new(StringComparer.Ordinal);

    public event EventHandler? Changed;

    public IReadOnlyList<ConnectionStatus> GetAll() => _statuses.Values.OrderBy(s => s.Name, StringComparer.Ordinal).ToList();

    public void Report(string name, ConnectionState state, string? detail)
    {
        _statuses[name] = new ConnectionStatus(name, state, detail, clock.GetUtcNow());
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
