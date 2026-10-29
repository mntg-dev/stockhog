using Stockhog.Infrastructure.Configuration;
using Stockhog.Infrastructure.Tests.TestSupport;

namespace Stockhog.Infrastructure.Tests.Configuration;

public class LegacyDataMigrationTests
{
    [Fact]
    public void Copies_the_old_app_data_folder_with_subfolders_and_keeps_the_original()
    {
        using var temp = new TempDirectory();
        File.WriteAllText(Write(temp.Combine("EmTrading", "settings.json")), "{\"culture\":\"en-US\"}");
        File.WriteAllText(Write(temp.Combine("EmTrading", "logs", "stockhog-20261029.log")), "log");

        Assert.True(LegacyDataMigration.CopyAppDataFolder(temp.Path));

        Assert.Equal("{\"culture\":\"en-US\"}", File.ReadAllText(temp.Combine("Stockhog", "settings.json")));
        Assert.True(File.Exists(temp.Combine("Stockhog", "logs", "stockhog-20261029.log")));
        Assert.True(File.Exists(temp.Combine("EmTrading", "settings.json")));
    }

    [Fact]
    public void An_existing_new_folder_is_left_alone()
    {
        using var temp = new TempDirectory();
        File.WriteAllText(Write(temp.Combine("EmTrading", "settings.json")), "old");
        File.WriteAllText(Write(temp.Combine("Stockhog", "settings.json")), "new");

        Assert.False(LegacyDataMigration.CopyAppDataFolder(temp.Path));
        Assert.Equal("new", File.ReadAllText(temp.Combine("Stockhog", "settings.json")));
    }

    [Fact]
    public void Nothing_happens_without_old_data()
    {
        using var temp = new TempDirectory();

        Assert.False(LegacyDataMigration.CopyAppDataFolder(temp.Path));
        Assert.False(LegacyDataMigration.CopyDatabase(temp.Combine("stockhog.db")));
        Assert.False(Directory.Exists(temp.Combine("Stockhog")));
    }

    [Fact]
    public void Copies_the_old_database_with_its_side_files_once()
    {
        using var temp = new TempDirectory();
        File.WriteAllText(temp.Combine("emtrading.db"), "db");
        File.WriteAllText(temp.Combine("emtrading.db-wal"), "wal");

        Assert.True(LegacyDataMigration.CopyDatabase(temp.Combine("stockhog.db")));
        Assert.Equal(("db", "wal"), (File.ReadAllText(temp.Combine("stockhog.db")), File.ReadAllText(temp.Combine("stockhog.db-wal"))));
        Assert.False(File.Exists(temp.Combine("stockhog.db-shm")));
        Assert.True(File.Exists(temp.Combine("emtrading.db")));

        // Second start: the new database exists, so it is not overwritten.
        File.WriteAllText(temp.Combine("stockhog.db"), "changed");
        Assert.False(LegacyDataMigration.CopyDatabase(temp.Combine("stockhog.db")));
        Assert.Equal("changed", File.ReadAllText(temp.Combine("stockhog.db")));
    }

    [Fact]
    public void A_database_configured_under_another_name_is_not_touched()
    {
        using var temp = new TempDirectory();
        File.WriteAllText(temp.Combine("emtrading.db"), "db");

        Assert.False(LegacyDataMigration.CopyDatabase(temp.Combine("custom.db")));
        Assert.False(File.Exists(temp.Combine("custom.db")));
    }

    private static string Write(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        return path;
    }
}
