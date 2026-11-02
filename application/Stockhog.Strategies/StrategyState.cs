using System.Text.Json;

namespace Stockhog.Strategies;

/// <summary>
/// Where a strategy stood when its run ended, before the final liquidation: what it holds, since when, and the stop it
/// would exit at. Written by <see cref="StrategyBase"/> to the file named in <see cref="EngineParameters.StateFile"/>,
/// so the app can tell what the strategy wants to hold today from the same code it backtests.
/// Times are in exchange (New York) time.
/// </summary>
/// <param name="AsOf">The algorithm time at the end of the run.</param>
/// <param name="LastBarTime">End of the last bar the strategy saw, or <c>null</c> if it saw none.</param>
/// <param name="Quantity">Signed: negative for a short position, 0 when flat.</param>
/// <param name="EntryTime">When the open position was entered; <c>null</c> when flat.</param>
/// <param name="StopPrice">The level the strategy would exit the open position at, if it uses one.</param>
/// <param name="LastExitTime">When the strategy last closed a position, if it did during the run.</param>
public sealed record StrategyState(
    DateTime AsOf,
    DateTime? LastBarTime,
    decimal LastPrice,
    decimal Quantity,
    decimal AveragePrice,
    DateTime? EntryTime,
    string? EntryTag,
    decimal? StopPrice,
    DateTime? LastExitTime,
    string? LastExitTag)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public void Save(string path) => File.WriteAllText(path, JsonSerializer.Serialize(this, JsonOptions));

    public static async Task<StrategyState?> LoadAsync(string path, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(path))
            return null;

        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<StrategyState>(stream, JsonOptions, cancellationToken).ConfigureAwait(false);
    }
}
