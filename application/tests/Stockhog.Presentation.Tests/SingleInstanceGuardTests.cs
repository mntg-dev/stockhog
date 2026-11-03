using Stockhog.Presentation.Common;

namespace Stockhog.Presentation.Tests;

public class SingleInstanceGuardTests
{
    [Fact]
    public void A_second_guard_with_the_same_name_is_not_the_first_instance_until_the_first_one_is_gone()
    {
        var name = @"Local\Stockhog.Tests." + Guid.NewGuid().ToString("N");

        var first = new SingleInstanceGuard(name);
        bool secondIsFirst;
        using (var second = new SingleInstanceGuard(name))
            secondIsFirst = second.IsFirstInstance;
        first.Dispose();
        using var third = new SingleInstanceGuard(name);

        Assert.True(first.IsFirstInstance);
        Assert.False(secondIsFirst);
        Assert.True(third.IsFirstInstance);
    }
}
