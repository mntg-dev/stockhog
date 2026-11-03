using Stockhog.Domain.Trading;
using Stockhog.Infrastructure.Trading;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Stockhog.Infrastructure.Tests.Trading;

public sealed class RiskLimitsStoreTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"stockhog-limits-{Guid.NewGuid():N}.json");

    public void Dispose() => File.Delete(_path);

    [Fact]
    public void Without_a_saved_file_the_configured_limits_apply()
    {
        var store = Create(new RiskOptions { MaxDailyLoss = 500 });

        Assert.Equal(500m, store.Current.MaxDailyLoss);
        Assert.False(File.Exists(_path));
    }

    [Fact]
    public void Changed_limits_and_the_kill_switch_survive_a_restart()
    {
        var changed = RiskLimits.Default with { TradingEnabled = false, MaxDailyLoss = 750m, MaxOpenPositions = 4, FlattenOnDrawdown = true };
        Create(new RiskOptions()).Update(changed);

        var afterRestart = Create(new RiskOptions { MaxDailyLoss = 2_000 });

        Assert.Equal(changed, afterRestart.Current);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("""{"tradingEnabled":true,"maxOrderNotional":0,"maxPositionNotional":1,"maxDailyLoss":1,"maxOpenOrders":1}""")]
    public void A_damaged_or_out_of_range_file_is_ignored(string content)
    {
        File.WriteAllText(_path, content);

        var store = Create(new RiskOptions { MaxDailyLoss = 900 });

        Assert.Equal(900m, store.Current.MaxDailyLoss);
    }

    private RiskLimitsStore Create(RiskOptions options) => new(Options.Create(options), NullLogger<RiskLimitsStore>.Instance, _path);
}
