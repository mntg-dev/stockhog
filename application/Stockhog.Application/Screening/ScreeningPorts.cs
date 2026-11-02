namespace Stockhog.Application.Screening;

/// <summary>
/// Daily bars for many symbols at once (the broker's multi-symbol endpoint), split-adjusted so prices before and
/// after a split are comparable.
/// </summary>
public interface IDailyBarSource
{
    /// <summary><c>false</c> when the credentials it needs are missing; nothing should be requested then.</summary>
    bool IsConfigured { get; }

    /// <summary>Most symbols one call should ask for; the screener splits the universe into batches of this size.</summary>
    int MaxSymbolsPerRequest { get; }

    /// <param name="start">First session to include.</param>
    /// <param name="end">Last session to include; a session still in progress is never returned.</param>
    /// <returns>Bars per symbol, oldest first. Symbols without data in the range are missing from the result.</returns>
    Task<IReadOnlyDictionary<string, IReadOnlyList<DailyBar>>> GetDailyBarsAsync(
        IReadOnlyList<string> symbols,
        DateOnly start,
        DateOnly end,
        CancellationToken cancellationToken = default);
}

/// <summary>Finished screener runs. Each is saved once and never changed; only the newest are kept.</summary>
public interface IScreenerResultStore
{
    Task SaveAsync(ScreenerResult result, CancellationToken cancellationToken = default);

    /// <returns>Newest first.</returns>
    Task<IReadOnlyList<ScreenerRunSummary>> ListRunsAsync(int take, CancellationToken cancellationToken = default);

    /// <param name="id">The run, or <c>null</c> for the newest one.</param>
    Task<ScreenerResult?> GetAsync(Guid? id, CancellationToken cancellationToken = default);
}

public static class ScreenerKeys
{
    /// <summary>Job key of a screener run, so a second run is not queued while one is running.</summary>
    public const string RunJobKey = "screener:run";

    public const string CacheTag = "screener";
}
