using Stockhog.Infrastructure.Configuration;
using Microsoft.Extensions.Options;

namespace Stockhog.Infrastructure.Lean.Data;

/// <summary>
/// Downloads the reference data Lean needs to run a backtest (market hours, symbol properties, interest rates).
/// Uses its own HttpClient so broker credentials are never sent to third-party hosts.
/// </summary>
internal sealed class LeanReferenceDataClient(HttpClient httpClient, IOptions<StorageOptions> storage)
{
    private static readonly string[] RequiredFiles =
    [
        "market-hours/market-hours-database.json",
        "symbol-properties/symbol-properties-database.csv",
        "alternative/interest-rate/usa/interest-rate.csv"
    ];

    public async Task EnsureReferenceDataAsync(CancellationToken cancellationToken)
    {
        foreach (var relativePath in RequiredFiles)
        {
            var target = Path.Combine(storage.Value.DataFolder, relativePath.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(target))
                continue;

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            var content = await httpClient.GetByteArrayAsync(relativePath, cancellationToken).ConfigureAwait(false);

            var tempFile = target + ".tmp";
            await File.WriteAllBytesAsync(tempFile, content, cancellationToken).ConfigureAwait(false);
            File.Move(tempFile, target, overwrite: true);
        }
    }
}
