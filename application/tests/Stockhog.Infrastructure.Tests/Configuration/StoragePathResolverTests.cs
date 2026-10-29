using Stockhog.Infrastructure.Configuration;
using Stockhog.Infrastructure.Tests.TestSupport;

namespace Stockhog.Infrastructure.Tests.Configuration;

public class StoragePathResolverTests
{
    [Fact]
    public void Empty_value_uses_default_folder_under_root()
    {
        using var root = new TempDirectory();

        Assert.Equal(root.Combine("TradingData"), StoragePathResolver.Resolve(root.Path, "", "TradingData"));
        Assert.Equal(root.Combine("TradingData"), StoragePathResolver.Resolve(root.Path, null, "TradingData"));
    }

    [Fact]
    public void Relative_value_is_resolved_against_root()
    {
        using var root = new TempDirectory();

        Assert.Equal(root.Combine("data", "lean"), StoragePathResolver.Resolve(root.Path, "data/lean", "TradingData"));
    }

    [Fact]
    public void Absolute_value_is_kept()
    {
        using var root = new TempDirectory();
        using var other = new TempDirectory();

        Assert.Equal(other.Path, StoragePathResolver.Resolve(root.Path, other.Path, "TradingData"));
    }

    [Fact]
    public void Default_root_is_the_folder_containing_the_solution_file()
    {
        using var root = new TempDirectory();
        File.WriteAllText(root.Combine("Solution.slnx"), "<Solution />");
        var nested = Directory.CreateDirectory(root.Combine("App", "bin", "Debug")).FullName;

        Assert.Equal(root.Path, StoragePathResolver.FindDefaultRoot(nested));
    }
}
