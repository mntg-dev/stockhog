using Stockhog.Application.Signals;
using Stockhog.Domain.Backtesting;
using Stockhog.Domain.MarketData;
using Stockhog.Infrastructure.Configuration;
using Stockhog.Infrastructure.Lean.Engine.Contracts;
using Stockhog.Strategies;
using Microsoft.Extensions.Options;

namespace Stockhog.Infrastructure.Lean.Engine.Results;

/// <summary>Reads the <c>{RunId}-state.json</c> a strategy writes next to its result (see <see cref="StrategyState"/>).</summary>
internal sealed class StrategyStateReader(IOptions<StorageOptions> storage) : IStrategyStateReader
{
    public async Task<StrategyEndState?> ReadAsync(Symbol symbol, BacktestRunId runId, CancellationToken cancellationToken = default)
    {
        var path = BacktestJob.StateFilePath(Path.Combine(storage.Value.ResultsFolder, symbol.Value), runId.Value);
        var state = await StrategyState.LoadAsync(path, cancellationToken).ConfigureAwait(false);

        return state is null
            ? null
            : new StrategyEndState(
                state.AsOf,
                state.LastBarTime,
                state.LastPrice,
                state.Quantity,
                state.AveragePrice,
                state.EntryTime,
                state.EntryTag,
                state.StopPrice,
                state.LastExitTime,
                state.LastExitTag);
    }
}
