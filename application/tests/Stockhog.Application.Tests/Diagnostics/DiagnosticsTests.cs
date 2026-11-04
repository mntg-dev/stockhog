using Stockhog.Application.Diagnostics;
using Stockhog.Application.Tests.TestSupport;
using Stockhog.Application.Trading.Safety;

namespace Stockhog.Application.Tests.Diagnostics;

public class DiagnosticsTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_export_takes_the_latest_audit_entries_along()
    {
        await using var host = new ApplicationTestHost();
        await host.AuditLog.RecordAsync(new AuditEntry(TestData.Now, Domain.Trading.TradingMode.Paper, AuditCategory.Order, "Filled", "emt-1", "AAPL", "10 filled"), Ct);

        var export = await host.Dispatcher.SendAsync(new ExportDiagnosticsCommand(@"C:\temp\diag.zip", new Dictionary<string, string>()), Ct);

        Assert.True(export.IsSuccess, export.Error?.Message);
        Assert.Contains("10 filled", Assert.Single(host.Diagnostics.AuditLines!));
    }

    [Theory]
    [InlineData("diag.zip")]               // not a full path
    [InlineData(@"C:\temp\diag.txt")]     // not a zip
    public async Task Only_a_full_path_to_a_zip_is_accepted(string path)
    {
        await using var host = new ApplicationTestHost();

        var export = await host.Dispatcher.SendAsync(new ExportDiagnosticsCommand(path, new Dictionary<string, string>()), Ct);

        Assert.True(export.IsFailure);
        Assert.Null(host.Diagnostics.AuditLines);
    }
}
