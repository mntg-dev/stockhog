using System.Text;

namespace Stockhog.EngineRunner;

/// <summary>
/// Writes protocol lines to the real process stdout. Lean redirects <see cref="Console.Out"/> into the algorithm log
/// while the engine runs, so <see cref="Console.WriteLine(string)"/> cannot be used for messages meant for the parent app.
/// </summary>
internal static class ProtocolWriter
{
    private static readonly Lock Sync = new();
    private static readonly StreamWriter Stdout = new(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true };

    public static void WriteLine(string line)
    {
        lock (Sync)
        {
            Stdout.WriteLine(line);
        }
    }
}
