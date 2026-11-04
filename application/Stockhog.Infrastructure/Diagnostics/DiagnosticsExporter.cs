using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using Stockhog.Application.Diagnostics;
using Stockhog.Infrastructure.Configuration;

namespace Stockhog.Infrastructure.Diagnostics;

/// <summary>
/// Builds the support zip from the app data folder: the last week of logs (newest first, up to a size cap), the user
/// settings and saved risk limits, plus the audit lines and facts handed in. The encrypted key files are never read,
/// and every text passes <see cref="Redact"/> on the way into the zip.
/// </summary>
internal sealed partial class DiagnosticsExporter(TimeProvider clock, string? appDataFolder = null) : IDiagnosticsExporter
{
    private const long MaxLogBytes = 20 * 1024 * 1024;
    private static readonly TimeSpan LogAge = TimeSpan.FromDays(7);
    private static readonly string[] SettingsFiles = ["settings.json", "risk-limits.json"];

    private readonly string _appData = appDataFolder ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), LegacyDataMigration.FolderName);

    public async Task<DiagnosticsExport> ExportAsync(string targetPath, IReadOnlyDictionary<string, string> facts, IReadOnlyList<string> auditLines,
        CancellationToken cancellationToken = default)
    {
        var files = 0;
        await using (var stream = new FileStream(targetPath, FileMode.Create, FileAccess.Write, FileShare.None))
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create))
        {
            var about = new StringBuilder()
                .AppendLine($"Exported: {clock.GetUtcNow():O}")
                .AppendLine($"OS: {Environment.OSVersion}")
                .AppendLine($".NET: {Environment.Version}");
            foreach (var (name, value) in facts)
                about.AppendLine($"{name}: {value}");
            await AddAsync(zip, "about.txt", about.ToString(), cancellationToken);
            await AddAsync(zip, "audit.tsv", string.Join(Environment.NewLine, auditLines), cancellationToken);
            files += 2;

            foreach (var name in SettingsFiles)
            {
                var path = Path.Combine(_appData, name);
                if (File.Exists(path))
                {
                    await AddAsync(zip, name, await ReadSharedAsync(path, cancellationToken), cancellationToken);
                    files++;
                }
            }

            var logs = Path.Combine(_appData, "logs");
            if (Directory.Exists(logs))
            {
                var since = clock.GetUtcNow().UtcDateTime - LogAge;
                long total = 0;
                foreach (var log in new DirectoryInfo(logs).GetFiles("*.log").Where(f => f.LastWriteTimeUtc >= since).OrderByDescending(f => f.LastWriteTimeUtc))
                {
                    if (total + log.Length > MaxLogBytes)
                        break;
                    total += log.Length;
                    await AddAsync(zip, "logs/" + log.Name, await ReadSharedAsync(log.FullName, cancellationToken), cancellationToken);
                    files++;
                }
            }
        }

        return new DiagnosticsExport(targetPath, files, new FileInfo(targetPath).Length);
    }

    /// <summary>Masks anything shaped like an Alpaca key id or an Anthropic key, keeping its first and last 4 characters.</summary>
    internal static string Redact(string text) => KeyPattern().Replace(text, m => m.Value.Length <= 8 ? "…" : $"{m.Value[..4]}…{m.Value[^4..]}");

    [GeneratedRegex(@"\b(?:[PA]K[A-Z0-9]{14,}|sk-ant-[A-Za-z0-9_\-]{10,})")]
    private static partial Regex KeyPattern();

    private static async Task AddAsync(ZipArchive zip, string name, string content, CancellationToken cancellationToken)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
        await using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
        await writer.WriteAsync(Redact(content).AsMemory(), cancellationToken);
    }

    /// <summary>The current log is still open for writing by the logger.</summary>
    private static async Task<string> ReadSharedAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        return await reader.ReadToEndAsync(cancellationToken);
    }
}
