namespace Stockhog.Infrastructure.Configuration;

/// <summary>
/// Carries data over from the app's former name, EmTrading: settings, the saved API key, logs and, when installed, data
/// and results in <c>%LOCALAPPDATA%\EmTrading</c>, and the history database <c>emtrading.db</c>. Files are copied, never
/// moved or deleted, and only when the new location does not exist yet, so it runs once and the old data stays as a
/// backup.
/// </summary>
public static class LegacyDataMigration
{
    public const string LegacyFolderName = "EmTrading";
    public const string FolderName = "Stockhog";
    public const string LegacyDatabaseName = "emtrading.db";
    public const string DatabaseName = "stockhog.db";

    /// <summary>SQLite keeps recent writes in these side files; they belong to the database.</summary>
    private static readonly string[] DatabaseFileSuffixes = ["", "-wal", "-shm"];

    /// <summary>Copies <c>EmTrading</c> to <c>Stockhog</c> inside <paramref name="localAppData"/>.</summary>
    /// <returns><c>true</c> if the folder was copied.</returns>
    public static bool CopyAppDataFolder(string localAppData)
    {
        var source = Path.Combine(localAppData, LegacyFolderName);
        var target = Path.Combine(localAppData, FolderName);
        if (!Directory.Exists(source) || Directory.Exists(target))
            return false;

        CopyDirectory(source, target);
        return true;
    }

    /// <summary>
    /// Copies <c>emtrading.db</c> next to <paramref name="databaseFile"/> to that path, when the database has the
    /// default name and does not exist yet.
    /// </summary>
    /// <returns><c>true</c> if the database was copied.</returns>
    public static bool CopyDatabase(string databaseFile)
    {
        if (!string.Equals(Path.GetFileName(databaseFile), DatabaseName, StringComparison.OrdinalIgnoreCase) || File.Exists(databaseFile))
            return false;

        var legacy = Path.Combine(Path.GetDirectoryName(databaseFile)!, LegacyDatabaseName);
        if (!File.Exists(legacy))
            return false;

        foreach (var suffix in DatabaseFileSuffixes)
        {
            if (File.Exists(legacy + suffix))
                File.Copy(legacy + suffix, databaseFile + suffix);
        }

        return true;
    }

    private static void CopyDirectory(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var file in Directory.EnumerateFiles(source))
            File.Copy(file, Path.Combine(target, Path.GetFileName(file)));
        foreach (var directory in Directory.EnumerateDirectories(source))
            CopyDirectory(directory, Path.Combine(target, Path.GetFileName(directory)));
    }
}
