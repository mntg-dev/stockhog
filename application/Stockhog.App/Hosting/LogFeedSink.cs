using Stockhog.Presentation.Logging;
using Serilog.Core;
using Serilog.Events;

namespace Stockhog.App.Hosting;

/// <summary>
/// Serilog sink that copies log events into the in-app log view's <see cref="LogFeed"/>.
/// Keeps Serilog out of the presentation layer.
/// </summary>
internal sealed class LogFeedSink(LogFeed feed) : ILogEventSink
{
    public void Emit(LogEvent logEvent)
    {
        var source = logEvent.Properties.TryGetValue("SourceContext", out var context)
            ? context.ToString().Trim('"').Split('.').Last()
            : string.Empty;

        feed.Append(new LogEntry(logEvent.Timestamp, Map(logEvent.Level), source, logEvent.RenderMessage()));
    }

    private static LogLevel Map(LogEventLevel level) => level switch
    {
        LogEventLevel.Verbose or LogEventLevel.Debug => LogLevel.Debug,
        LogEventLevel.Information => LogLevel.Information,
        LogEventLevel.Warning => LogLevel.Warning,
        _ => LogLevel.Error
    };
}
