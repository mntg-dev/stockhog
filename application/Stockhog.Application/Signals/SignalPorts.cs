using Stockhog.Application.Abstractions.Results;
using Stockhog.Domain.Backtesting;
using Stockhog.Domain.MarketData;

namespace Stockhog.Application.Signals;

/// <summary>
/// Where a strategy stood when a run ended, before its final liquidation, as the engine reported it.
/// Times are in exchange (New York) time.
/// </summary>
/// <param name="Quantity">Signed: negative for a short position, 0 when flat.</param>
public sealed record StrategyEndState(
    DateTime AsOf,
    DateTime? LastBarTime,
    decimal LastPrice,
    decimal Quantity,
    decimal AveragePrice,
    DateTime? EntryTime,
    string? EntryTag,
    decimal? StopPrice,
    DateTime? LastExitTime,
    string? LastExitTag);

/// <summary>Reads the end state the engine stored next to a run's result.</summary>
public interface IStrategyStateReader
{
    /// <returns>The state, or <c>null</c> if the run stored none.</returns>
    Task<StrategyEndState?> ReadAsync(Symbol symbol, BacktestRunId runId, CancellationToken cancellationToken = default);
}

public static class SignalErrors
{
    public static Error NoState(string runId) => Error.Failure(
        "Signal.NoState",
        $"Run {runId} stored no strategy state, so its current position is unknown.");
}
