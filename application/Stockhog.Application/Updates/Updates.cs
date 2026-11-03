using Stockhog.Application.Abstractions.Messaging;
using Stockhog.Application.Abstractions.Results;

namespace Stockhog.Application.Updates;

/// <summary>The newest published release, as the release workflow describes it in its manifest.</summary>
public sealed record ReleaseInfo(string Version, DateTimeOffset? PublishedAt, Uri? InstallerUrl);

/// <summary>Reads the release manifest. Returns null when it cannot be read: an update check never gets in the way.</summary>
public interface IReleaseFeed
{
    Task<ReleaseInfo?> GetLatestAsync(CancellationToken cancellationToken = default);
}

/// <summary>Whether a newer release than <paramref name="CurrentVersion"/> is published.</summary>
public sealed record CheckForUpdateQuery(string CurrentVersion) : IQuery<UpdateCheck>;

/// <param name="InstallerUrl">Only an HTTPS link is passed on.</param>
public sealed record UpdateCheck(bool IsAvailable, string? LatestVersion, Uri? InstallerUrl)
{
    public static UpdateCheck None { get; } = new(false, null, null);
}

public static class AppVersions
{
    /// <summary>
    /// Reads <c>v1.2.3</c>, <c>1.2.3-beta.1</c> or <c>1.2.3+commit</c> as 1.2.3; pre-release and build parts are ignored,
    /// so a pre-release never counts as newer than its release.
    /// </summary>
    public static Version? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        var core = text.Trim().TrimStart('v', 'V').Split('-', '+')[0];
        return Version.TryParse(core, out var version) ? version : null;
    }

    public static bool IsNewer(string? latest, string? current) =>
        Parse(latest) is { } newer && Parse(current) is { } installed && newer > installed;
}

internal sealed class CheckForUpdateQueryHandler(IReleaseFeed feed) : IQueryHandler<CheckForUpdateQuery, UpdateCheck>
{
    public async Task<Result<UpdateCheck>> HandleAsync(CheckForUpdateQuery query, CancellationToken cancellationToken)
    {
        if (await feed.GetLatestAsync(cancellationToken) is not { } latest || !AppVersions.IsNewer(latest.Version, query.CurrentVersion))
            return UpdateCheck.None;

        var installer = latest.InstallerUrl is { Scheme: "https" } url ? url : null;
        return new UpdateCheck(true, latest.Version.TrimStart('v', 'V'), installer);
    }
}
