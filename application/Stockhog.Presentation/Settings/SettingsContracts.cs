namespace Stockhog.Presentation.Settings;

/// <summary>
/// Per-user preferences, saved between sessions.
/// </summary>
public sealed record UserSettings
{
    /// <summary>Empty until the user picks one: the Windows language decides then.</summary>
    public string Culture { get; init; } = "";

    public string DefaultSymbols { get; init; } = "AAPL";

    public decimal DefaultInitialCapital { get; init; } = 100_000m;

    /// <summary>Windows notifications for fills, rejections, trading halts and autopilot orders.</summary>
    public bool ShowNotifications { get; init; } = true;

    /// <summary>The version of <see cref="TermsOfUse"/> the user accepted; 0 until they do.</summary>
    public int AcceptedTermsVersion { get; init; }
}

/// <summary>
/// The risk notice every user accepts before the first start: not investment advice, trading can lose money, the user
/// decides every order. Raising <see cref="CurrentVersion"/> asks everyone again.
/// </summary>
public static class TermsOfUse
{
    public const int CurrentVersion = 1;

    public static bool NeedsAcceptance(UserSettings settings) => settings.AcceptedTermsVersion < CurrentVersion;

    public static UserSettings Accept(UserSettings settings) => settings with { AcceptedTermsVersion = CurrentVersion };
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
    int MaxParallelJobs,
    string Version = "0.0.0");
