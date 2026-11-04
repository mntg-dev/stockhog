using System.IO.Compression;
using Stockhog.Infrastructure.Diagnostics;
using Microsoft.Extensions.Time.Testing;

namespace Stockhog.Infrastructure.Tests.Diagnostics;

public sealed class DiagnosticsExporterTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly DateTimeOffset Now = new(2026, 11, 4, 12, 0, 0, TimeSpan.Zero);

    private readonly string _folder = Path.Combine(Path.GetTempPath(), $"stockhog-diag-{Guid.NewGuid():N}");

    public DiagnosticsExporterTests() => Directory.CreateDirectory(Path.Combine(_folder, "logs"));

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    [Fact]
    public async Task The_zip_holds_recent_logs_settings_audit_and_facts_but_never_the_key_files()
    {
        File.WriteAllText(Path.Combine(_folder, "settings.json"), """{"culture":"en-US"}""");
        File.WriteAllText(Path.Combine(_folder, "risk-limits.json"), """{"maxDailyLoss":1000}""");
        File.WriteAllText(Path.Combine(_folder, "alpaca-keys.bin"), "encrypted");
        File.WriteAllText(Path.Combine(_folder, "anthropic-key.bin"), "encrypted");
        Log("stockhog-20261104.log", "today", Now);
        Log("stockhog-20261020.log", "two weeks ago", Now.AddDays(-15));
        var target = Path.Combine(_folder, "out.zip");

        var export = await new DiagnosticsExporter(new FakeTimeProvider(Now), _folder)
            .ExportAsync(target, new Dictionary<string, string> { ["Version"] = "0.4.0" }, ["2026-11-04\tPaper\tOrder\tFilled"], Ct);

        using var zip = ZipFile.OpenRead(target);
        Assert.Equal(["about.txt", "audit.tsv", "logs/stockhog-20261104.log", "risk-limits.json", "settings.json"], zip.Entries.Select(e => e.FullName).Order());
        Assert.Equal(5, export.Files);
        Assert.Contains("Version: 0.4.0", Read(zip, "about.txt"));
        Assert.Contains("Filled", Read(zip, "audit.tsv"));
    }

    [Fact]
    public async Task Anything_that_looks_like_a_key_is_masked()
    {
        Log("stockhog-20261104.log", "key PKABCDEFGHIJKLMNOP12 and sk-ant-api03-abcdefghijklmnop in a log", Now);
        var target = Path.Combine(_folder, "out.zip");

        await new DiagnosticsExporter(new FakeTimeProvider(Now), _folder).ExportAsync(target, new Dictionary<string, string>(), [], Ct);

        using var zip = ZipFile.OpenRead(target);
        var log = Read(zip, "logs/stockhog-20261104.log");
        Assert.DoesNotContain("PKABCDEFGHIJKLMNOP12", log);
        Assert.DoesNotContain("abcdefghijklmnop", log);
        Assert.Contains("PKAB…OP12", log);
    }

    private void Log(string name, string text, DateTimeOffset written)
    {
        var path = Path.Combine(_folder, "logs", name);
        File.WriteAllText(path, text);
        File.SetLastWriteTimeUtc(path, written.UtcDateTime);
    }

    private static string Read(ZipArchive zip, string name)
    {
        using var reader = new StreamReader(zip.GetEntry(name)!.Open());
        return reader.ReadToEnd();
    }
}
