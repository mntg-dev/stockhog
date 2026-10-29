namespace Stockhog.Application.Instruments;

/// <summary>A tradable instrument, e.g. AAPL "Apple Inc. Common Stock" on NASDAQ.</summary>
public sealed record InstrumentInfo(string Symbol, string Name, string Exchange);

/// <param name="Count">Instruments stored locally.</param>
/// <param name="RefreshedAt">When the list was last downloaded, or <c>null</c> if never.</param>
public sealed record InstrumentCatalogStatus(int Count, DateTimeOffset? RefreshedAt);

/// <summary>
/// Where the instrument list comes from (the broker). Returns active, tradable US equities and ETFs.
/// </summary>
public interface IInstrumentSource
{
    /// <summary><c>false</c> when the credentials it needs are missing; nothing should be requested then.</summary>
    bool IsConfigured { get; }

    Task<IReadOnlyList<InstrumentInfo>> GetTradableInstrumentsAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// The locally stored instrument list. Reference data, replaced as a whole on every refresh.
/// </summary>
public interface IInstrumentStore
{
    Task<InstrumentCatalogStatus> GetStatusAsync(CancellationToken cancellationToken = default);

    /// <returns>All instruments, ordered by symbol.</returns>
    Task<IReadOnlyList<InstrumentInfo>> ListAsync(CancellationToken cancellationToken = default);

    /// <summary>Replaces the stored list in one transaction, so readers never see a half-written list.</summary>
    Task ReplaceAllAsync(IReadOnlyList<InstrumentInfo> instruments, DateTimeOffset refreshedAt, CancellationToken cancellationToken = default);
}

public static class InstrumentCacheKeys
{
    public const string Tag = "instruments";

    /// <summary>Job key of the refresh, so a second refresh is not queued while one is running.</summary>
    public const string RefreshJobKey = "instruments:refresh";
}
