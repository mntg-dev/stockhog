using System.Globalization;

namespace Stockhog.Infrastructure.Lean.Engine.Contracts;

/// <summary>
/// Line protocol the engine runner writes to stdout. Lean's own log lines pass through as <see cref="EngineMessageKind.Output"/>.
/// </summary>
public static class EngineProtocol
{
    private const string Prefix = "@@emt ";

    public static string Progress(int percent) => $"{Prefix}progress {Math.Clamp(percent, 0, 100)}";

    public static string Log(string message) => $"{Prefix}log {SingleLine(message)}";

    public static string Error(string message) => $"{Prefix}error {SingleLine(message)}";

    public static string Completed(string runId) => $"{Prefix}completed {runId}";

    public static EngineMessage Parse(string? line)
    {
        if (string.IsNullOrEmpty(line) || !line.StartsWith(Prefix, StringComparison.Ordinal))
            return new EngineMessage(EngineMessageKind.Output, line ?? string.Empty);

        var body = line.AsSpan(Prefix.Length);
        var space = body.IndexOf(' ');
        var command = space < 0 ? body : body[..space];
        var argument = space < 0 ? string.Empty : body[(space + 1)..].ToString();

        return command switch
        {
            "progress" when int.TryParse(argument, NumberStyles.Integer, CultureInfo.InvariantCulture, out var percent)
                => new EngineMessage(EngineMessageKind.Progress, argument, Math.Clamp(percent, 0, 100)),
            "log" => new EngineMessage(EngineMessageKind.Log, argument),
            "error" => new EngineMessage(EngineMessageKind.Error, argument),
            "completed" => new EngineMessage(EngineMessageKind.Completed, argument),
            _ => new EngineMessage(EngineMessageKind.Output, line)
        };
    }

    private static string SingleLine(string message) => message.ReplaceLineEndings(" ").Trim();
}

public enum EngineMessageKind
{
    Output,
    Progress,
    Log,
    Error,
    Completed
}

public readonly record struct EngineMessage(EngineMessageKind Kind, string Text, int Percent = 0);
