using Stockhog.Domain.Backtesting;
using Stockhog.Domain.Common;
using Stockhog.Domain.MarketData;

namespace Stockhog.Application.Backtesting;

/// <summary>
/// What the engine needs to execute one <see cref="BacktestRun"/>.
/// </summary>
/// <param name="ForSignal">The run asks what the strategy wants to do next, so it also decides on the last bars.</param>
public sealed record BacktestRequest(
    BacktestRunId RunId,
    StrategyName Strategy,
    Symbol Symbol,
    DateRange Period,
    Money InitialCapital,
    StrategyParameters Parameters,
    bool ForSignal = false)
{
    public static BacktestRequest For(BacktestRun run) =>
        new(run.Id, run.Strategy, run.Symbol, run.Period, run.InitialCapital, run.Parameters);
}

/// <param name="Percent">0 to 100.</param>
public readonly record struct BacktestProgress(int Percent, string? Message = null);
