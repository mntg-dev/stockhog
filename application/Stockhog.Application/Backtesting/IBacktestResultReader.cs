using Stockhog.Domain.Backtesting;
using Stockhog.Domain.MarketData;

namespace Stockhog.Application.Backtesting;

public interface IBacktestResultReader
{
    /// <returns>The report, or <c>null</c> if the run has no stored result.</returns>
    Task<BacktestReport?> ReadAsync(Symbol symbol, BacktestRunId runId, CancellationToken cancellationToken = default);

    /// <returns>The most recent report for the symbol and strategy, or <c>null</c> if none exists.</returns>
    Task<BacktestReport?> ReadLatestAsync(Symbol symbol, StrategyName strategy, CancellationToken cancellationToken = default);
}
