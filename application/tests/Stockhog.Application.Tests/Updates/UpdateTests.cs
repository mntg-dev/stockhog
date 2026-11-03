using Stockhog.Application.Tests.TestSupport;
using Stockhog.Application.Updates;

namespace Stockhog.Application.Tests.Updates;

public class UpdateTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("v0.4.0", "0.3.0", true)]
    [InlineData("v0.3.0", "0.3.0", false)]
    [InlineData("v0.3.0", "0.4.0", false)]
    [InlineData("v0.10.0", "0.9.5", true)]          // compared as numbers, not text
    [InlineData("v0.4.0-beta.1", "0.4.0", false)]   // a pre-release never beats its release
    [InlineData("v0.4.0", "0.3.0+abc123", true)]    // build metadata is ignored
    [InlineData("garbage", "0.3.0", false)]
    public void Versions_are_compared_by_number(string latest, string current, bool newer) =>
        Assert.Equal(newer, AppVersions.IsNewer(latest, current));

    [Fact]
    public async Task A_newer_release_is_offered_with_its_installer()
    {
        await using var host = new ApplicationTestHost();
        var url = new Uri("https://emtradingstorage.blob.core.windows.net/releases/v0.4.0/Stockhog-v0.4.0-win-x64.msi");
        host.Releases.Latest = new ReleaseInfo("v0.4.0", TestData.Now, url);

        var check = await host.Dispatcher.SendAsync(new CheckForUpdateQuery("0.3.0"), Ct);

        Assert.Equal(new UpdateCheck(true, "0.4.0", url), check.Value);
    }

    [Fact]
    public async Task Nothing_is_offered_when_up_to_date_offline_or_for_a_link_that_is_not_https()
    {
        await using var host = new ApplicationTestHost();

        var offline = await host.Dispatcher.SendAsync(new CheckForUpdateQuery("0.3.0"), Ct);
        host.Releases.Latest = new ReleaseInfo("v0.3.0", TestData.Now, new Uri("https://example.com/a.msi"));
        var upToDate = await host.Dispatcher.SendAsync(new CheckForUpdateQuery("0.3.0"), Ct);
        host.Releases.Latest = new ReleaseInfo("v0.4.0", TestData.Now, new Uri("http://example.com/a.msi"));
        var plainHttp = await host.Dispatcher.SendAsync(new CheckForUpdateQuery("0.3.0"), Ct);

        Assert.Equal((UpdateCheck.None, UpdateCheck.None), (offline.Value, upToDate.Value));
        Assert.Equal((true, (Uri?)null), (plainHttp.Value.IsAvailable, plainHttp.Value.InstallerUrl));
    }
}
