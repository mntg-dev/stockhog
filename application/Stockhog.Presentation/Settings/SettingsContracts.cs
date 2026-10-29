namespace Stockhog.Presentation.Settings;

/// <summary>
/// Per-user preferences, saved between sessions.
/// </summary>
public sealed record UserSettings
{
    public string Culture { get; init; } = "pl-PL";

    public string DefaultSymbols { get; init; } = "AAPL";

    public decimal DefaultInitialCapital { get; init; } = 100_000m;
}

/// <summary>Implemented by the host (a JSON file per user).</summary>
public interface IUserSettingsStore
{
    UserSettings Load();

    void Save(UserSettings settings);
}

/// <summary>
/// Read-only facts about this installation, provided by the host from its configuration.
/// </summary>
public sealed record AppEnvironmentInfo(
    string DataFolder,
    string ResultsFolder,
    string DatabaseFile,
    string LogFolder,
    bool AlpacaCredentialsConfigured,
    int MaxParallelJobs);
