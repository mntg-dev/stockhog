using Stockhog.Application.Brokers;
using Stockhog.Application.Tests.TestSupport;

namespace Stockhog.Application.Tests.Brokers;

public class BrokerKeysTests
{
    private const string Key = "PKDATA0000000000ABCD";
    private const string Secret = "secret00000000000000000000000000000000001";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Keys_are_tested_before_they_are_stored_and_used_from_the_next_start()
    {
        await using var host = new ApplicationTestHost();

        var saved = await host.Dispatcher.SendAsync(new SaveBrokerKeysCommand($"  {Key} ", Secret), Ct);

        Assert.True(saved.IsSuccess, saved.Error?.Message);
        Assert.Equal(new BrokerKeys(Key, Secret), host.BrokerKeys.Stored);
        Assert.Equal(host.BrokerKeys.Stored, Assert.Single(host.BrokerKeyTester.Tested));
        Assert.True(saved.Value.RestartNeeded);
        var audit = Assert.Single(host.AuditLog.Entries, e => e.Action == "KeysSaved");
        Assert.DoesNotContain(Secret, audit.Details);
        Assert.Contains("PKDA…ABCD", audit.Details);
    }

    [Fact]
    public async Task Keys_the_broker_refuses_are_not_stored()
    {
        await using var host = new ApplicationTestHost();
        host.BrokerKeyTester.Result = new BrokerKeyCheck("the key or secret was not accepted.", null, null);

        var saved = await host.Dispatcher.SendAsync(new SaveBrokerKeysCommand(Key, Secret), Ct);

        Assert.True(saved.IsFailure);
        Assert.Contains("Market data: the key or secret was not accepted.", saved.Error.Message);
        Assert.Null(host.BrokerKeys.Stored);
    }

    [Theory]
    [InlineData("short", Secret, null, null)]
    [InlineData(Key, "with space in the secret 00000000000000", null, null)]
    [InlineData(Key, Secret, "PKPAPER000000000WXYZ", null)]   // a paper key without its secret
    public async Task Keys_that_cannot_be_right_are_refused_before_calling_the_broker(string key, string secret, string? paperKey, string? paperSecret)
    {
        await using var host = new ApplicationTestHost();

        var tested = await host.Dispatcher.SendAsync(new TestBrokerKeysCommand(key, secret, paperKey, paperSecret), Ct);

        Assert.True(tested.IsFailure);
        Assert.Empty(host.BrokerKeyTester.Tested);
    }

    [Fact]
    public async Task The_status_shows_where_the_keys_come_from_masked()
    {
        await using var host = new ApplicationTestHost();
        host.BrokerKeys.SourceInUse = BrokerKeySource.Settings;
        host.BrokerKeys.KeyInUse = Key;
        host.BrokerKeys.Stored = new BrokerKeys(Key, Secret);

        var status = await host.Dispatcher.SendAsync(new GetBrokerKeyStatusQuery(), Ct);
        var removed = await host.Dispatcher.SendAsync(new RemoveBrokerKeysCommand(), Ct);

        Assert.Equal(new BrokerKeyStatus(BrokerKeySource.Settings, "PKDA…ABCD", true, false), status.Value);
        Assert.Equal((false, true), (removed.Value.HasStoredKeys, removed.Value.RestartNeeded));
        Assert.Null(host.BrokerKeys.Stored);
        Assert.Contains(host.AuditLog.Entries, e => e.Action == "KeysRemoved");
    }

    [Fact]
    public void Commands_and_keys_never_print_their_secrets()
    {
        var keys = new BrokerKeys(Key, Secret, "PKPAPER000000000WXYZ", "paper-secret-000000000000000000000001");

        Assert.DoesNotContain(Secret, keys.ToString());
        Assert.DoesNotContain("paper-secret", keys.ToString());
        Assert.DoesNotContain(Secret, new SaveBrokerKeysCommand(Key, Secret).ToString());
        Assert.DoesNotContain(Secret, new TestBrokerKeysCommand(Key, Secret).ToString());
    }
}
