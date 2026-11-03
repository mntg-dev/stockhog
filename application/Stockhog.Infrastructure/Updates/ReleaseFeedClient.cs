using System.ComponentModel.DataAnnotations;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Stockhog.Application.Updates;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Stockhog.Infrastructure.Updates;

public sealed class UpdateOptions
{
    public const string SectionName = "Updates";

    /// <summary>Off for builds that are updated some other way.</summary>
    public bool CheckOnStart { get; set; } = true;

    /// <summary>Written by the release workflow next to the installers.</summary>
    [Required, Url]
    public string ManifestUrl { get; set; } = "https://emtradingstorage.blob.core.windows.net/releases/latest.json";
}

/// <summary>
/// Reads <c>latest.json</c> from the release storage. Any failure (offline, a changed format) means "no update known":
/// the check is logged and otherwise silent.
/// </summary>
internal sealed class ReleaseFeedClient(HttpClient http, IOptions<UpdateOptions> options, ILogger<ReleaseFeedClient> logger) : IReleaseFeed
{
    public async Task<ReleaseInfo?> GetLatestAsync(CancellationToken cancellationToken = default)
    {
        if (!options.Value.CheckOnStart)
            return null;

        try
        {
            var manifest = await http.GetFromJsonAsync<ManifestDto>(options.Value.ManifestUrl, cancellationToken).ConfigureAwait(false);
            if (manifest?.Version is not { Length: > 0 } version)
                return null;

            var installer = Uri.TryCreate(manifest.Msi?.Url, UriKind.Absolute, out var url) ? url : null;
            return new ReleaseInfo(version, manifest.PublishedAt, installer);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or NotSupportedException
                                   || ex is TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            logger.LogInformation("Update check skipped: {Message}", ex.Message);
            return null;
        }
    }

    private sealed record ManifestDto(
        [property: JsonPropertyName("version")] string? Version,
        [property: JsonPropertyName("publishedAt")] DateTimeOffset? PublishedAt,
        [property: JsonPropertyName("msi")] FileDto? Msi);

    private sealed record FileDto([property: JsonPropertyName("url")] string? Url);
}
