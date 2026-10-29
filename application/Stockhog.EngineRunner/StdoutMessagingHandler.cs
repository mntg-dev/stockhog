using Stockhog.Infrastructure.Lean.Engine.Contracts;
using QuantConnect.Interfaces;
using QuantConnect.Notifications;
using QuantConnect.Packets;

namespace Stockhog.EngineRunner;

/// <summary>
/// Forwards Lean's packets to the parent app over stdout using <see cref="EngineProtocol"/>.
/// Lean calls this from several threads (result handler, algorithm); <see cref="ProtocolWriter"/> serializes the writes.
/// </summary>
internal sealed class StdoutMessagingHandler : IMessagingHandler
{
    private int _lastPercent = -1;

    public bool HasSubscribers { get; set; } = true;

    /// <summary>
    /// Set when the algorithm threw; Lean still finishes the run normally in that case.
    /// </summary>
    public string? RuntimeError { get; private set; }

    public void Initialize(MessagingHandlerInitializeParameters initializeParameters)
    {
    }

    public void SetAuthentication(AlgorithmNodePacket job)
    {
    }

    public void Send(Packet packet)
    {
        switch (packet)
        {
            case BacktestResultPacket result:
                ReportProgress((int)Math.Floor(result.Progress * 100));
                break;
            case DebugPacket debug:
                Write(EngineProtocol.Log(debug.Message));
                break;
            case LogPacket log:
                Write(EngineProtocol.Log(log.Message));
                break;
            case RuntimeErrorPacket error:
                RuntimeError = error.Message;
                Write(EngineProtocol.Error(error.Message));
                break;
            case HandledErrorPacket error:
                // e.g. a rejected order; the run continues, so it is reported as a log line, not a failure.
                Write(EngineProtocol.Log(error.Message));
                break;
        }
    }

    public void SendNotification(Notification notification)
    {
    }

    public void Dispose()
    {
    }

    private void ReportProgress(int percent)
    {
        // Lean sends a result packet every few seconds; only forward changes.
        var previous = Volatile.Read(ref _lastPercent);
        if (percent > previous && Interlocked.CompareExchange(ref _lastPercent, percent, previous) == previous)
            ProtocolWriter.WriteLine(EngineProtocol.Progress(percent));
    }

    private static void Write(string line) => ProtocolWriter.WriteLine(line);
}
