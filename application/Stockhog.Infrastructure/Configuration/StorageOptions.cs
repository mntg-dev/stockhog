using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.Options;

namespace Stockhog.Infrastructure.Configuration;

/// <summary>
/// Folders shared by the app and the engine runner. Empty or relative values are resolved by <see cref="StoragePathResolver"/>.
/// </summary>
public sealed class StorageOptions
{
    public const string SectionName = "Storage";

    [Required]
    public string DataFolder { get; set; } = string.Empty;

    [Required]
    public string ResultsFolder { get; set; } = string.Empty;

    /// <summary>SQLite database with the backtest history.</summary>
    [Required]
    public string DatabaseFile { get; set; } = string.Empty;
}

internal sealed class StorageOptionsPostConfigure : IPostConfigureOptions<StorageOptions>
{
    public void PostConfigure(string? name, StorageOptions options)
    {
        var root = StoragePathResolver.FindDefaultRoot(AppContext.BaseDirectory);
        options.DataFolder = StoragePathResolver.Resolve(root, options.DataFolder, "TradingData");
        options.ResultsFolder = StoragePathResolver.Resolve(root, options.ResultsFolder, "Results");
        options.DatabaseFile = StoragePathResolver.Resolve(root, options.DatabaseFile, "stockhog.db");
    }
}

internal static class StoragePathResolver
{
    /// <summary>
    /// Development: the folder containing the solution file, so the app and engine runner share data.
    /// Deployed: %LOCALAPPDATA%\Stockhog.
    /// </summary>
    public static string FindDefaultRoot(string startDirectory)
    {
        for (var dir = new DirectoryInfo(startDirectory); dir is not null; dir = dir.Parent)
        {
            if (dir.EnumerateFiles("*.slnx").Any())
                return dir.FullName;
        }

        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Stockhog");
    }

    public static string Resolve(string root, string? configured, string defaultFolderName)
    {
        var path = string.IsNullOrWhiteSpace(configured) ? defaultFolderName : Environment.ExpandEnvironmentVariables(configured);
        return Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(root, path));
    }
}
