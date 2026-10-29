using Stockhog.Infrastructure.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Stockhog.Infrastructure.Lean.Data;

/// <summary>
/// Creates the Lean data folder layout when the app starts.
/// </summary>
internal sealed class LeanDataFolderInitializer(IOptions<StorageOptions> storage, ILogger<LeanDataFolderInitializer> logger)
    : IHostedService
{
    private static readonly Dictionary<string, string[]> Structure = new()
    {
        ["equity"] = ["usa/daily", "usa/hour", "usa/minute", "usa/second", "usa/tick", "usa/fundamental", "usa/shortable", "usa/map_files", "usa/factor_files"],
        ["crypto"] = ["binance/daily", "binance/hour", "binance/minute", "coinbase/daily", "coinbase/hour", "coinbase/minute"],
        ["forex"] = ["oanda/daily", "oanda/hour", "oanda/minute", "fxcm/daily", "fxcm/hour", "fxcm/minute"],
        ["cfd"] = ["oanda/daily", "oanda/hour", "oanda/minute"],
        ["future"] = ["usa/daily", "usa/minute", "usa/margins"],
        ["option"] = ["usa/daily", "usa/minute"],
        ["index"] = ["usa/daily", "usa/minute"],
        ["alternative"] = [],
        ["cryptofuture"] = [],
        ["futureoption"] = [],
        ["indexoption"] = [],
        ["market-hours"] = [],
        ["symbol-properties"] = []
    };

    public Task StartAsync(CancellationToken cancellationToken)
    {
        var dataFolder = storage.Value.DataFolder;
        Initialize(dataFolder);
        Directory.CreateDirectory(storage.Value.ResultsFolder);

        logger.LogInformation("Data folder: {DataFolder}, results folder: {ResultsFolder}", dataFolder, storage.Value.ResultsFolder);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    internal static void Initialize(string dataFolder)
    {
        foreach (var (root, subFolders) in Structure)
        {
            var rootPath = Path.Combine(dataFolder, root);
            Directory.CreateDirectory(rootPath);

            foreach (var subFolder in subFolders)
                Directory.CreateDirectory(Path.Combine(rootPath, subFolder.Replace('/', Path.DirectorySeparatorChar)));
        }
    }
}
