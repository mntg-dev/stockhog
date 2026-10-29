using Stockhog.Application.Abstractions.Messaging;
using Stockhog.Application.Abstractions.Results;
using Stockhog.Application.Backtesting;
using Stockhog.Application.Backtesting.GetBacktestReport;
using Stockhog.Application.Common;
using FluentValidation;

namespace Stockhog.Application.Analytics;

public static class AnalyticsErrors
{
    public static Error NotEnoughTrades(int trades) =>
        Error.Failure("Analytics.NotEnoughTrades", $"The run has {trades} closed trade(s); at least {SimulateMonteCarloQuery.MinimumTrades} are needed.");

    public static Error NoEquity(string runId) =>
        Error.Failure("Analytics.NoEquity", $"Run {runId} has no equity curve.");
}

/// <summary>
/// Monte Carlo resampling of a finished run's trades. The report and the trades never change, so the result
/// is cached per run and settings; the same seed gives the same result.
/// </summary>
public sealed record SimulateMonteCarloQuery(
    string Symbol,
    string RunId,
    int Simulations = 10_000,
    MonteCarloMethod Method = MonteCarloMethod.Shuffle,
    int Seed = 42) : IQuery<MonteCarloResult>, ICacheableQuery
{
    public const int MinimumTrades = 2;
    public const int MaxSimulations = 200_000;

    public string CacheKey => $"analytics:montecarlo:{Symbol.Trim().ToUpperInvariant()}:{RunId}:{Method}:{Simulations}:{Seed}";

    public IReadOnlyCollection<string> CacheTags => [];

    public TimeSpan CacheDuration => TimeSpan.FromMinutes(30);
}

internal sealed class SimulateMonteCarloQueryValidator : AbstractValidator<SimulateMonteCarloQuery>
{
    public SimulateMonteCarloQueryValidator()
    {
        RuleFor(q => q.Symbol).MustBeSymbol();
        RuleFor(q => q.RunId).MustBeRunId();
        RuleFor(q => q.Simulations).InclusiveBetween(100, SimulateMonteCarloQuery.MaxSimulations)
            .WithMessage($"Run between 100 and {SimulateMonteCarloQuery.MaxSimulations:N0} simulations.");
        RuleFor(q => q.Method).IsInEnum();
    }
}

internal sealed class SimulateMonteCarloQueryHandler(IDispatcher dispatcher) : IQueryHandler<SimulateMonteCarloQuery, MonteCarloResult>
{
    public async Task<Result<MonteCarloResult>> HandleAsync(SimulateMonteCarloQuery query, CancellationToken cancellationToken)
    {
        var report = await dispatcher.SendAsync(new GetBacktestReportQuery(query.Symbol, query.RunId), cancellationToken);
        if (report.IsFailure)
            return report.Error;

        var trades = report.Value.ClosedTrades;
        if (trades.Count < SimulateMonteCarloQuery.MinimumTrades)
            return AnalyticsErrors.NotEnoughTrades(trades.Count);
        if (report.Value.Equity is not [var first, ..] || first.Value <= 0)
            return AnalyticsErrors.NoEquity(query.RunId);

        var profits = trades.Select(t => (double)t.ProfitLoss).ToArray();
        var settings = new MonteCarloSettings(query.Simulations, query.Method, query.Seed);

        // CPU-bound: run on the thread pool so a caller on the UI thread stays responsive while all cores work.
        return await Task.Run(() => MonteCarloSimulator.Run((double)first.Value, profits, settings, cancellationToken), cancellationToken);
    }
}

/// <summary>The deepest drawdowns of a finished run: when they started, bottomed out and recovered.</summary>
public sealed record GetDrawdownAnalysisQuery(string Symbol, string RunId, int Top = 5) : IQuery<DrawdownAnalysis>, ICacheableQuery
{
    public string CacheKey => $"analytics:drawdowns:{Symbol.Trim().ToUpperInvariant()}:{RunId}:{Top}";

    public IReadOnlyCollection<string> CacheTags => [];

    public TimeSpan CacheDuration => TimeSpan.FromMinutes(30);
}

/// <param name="TimeUnderwaterPercent">Share of equity points below the running peak.</param>
public sealed record DrawdownAnalysis(decimal MaxDrawdownPercent, double TimeUnderwaterPercent, IReadOnlyList<DrawdownPeriod> Periods);

internal sealed class GetDrawdownAnalysisQueryValidator : AbstractValidator<GetDrawdownAnalysisQuery>
{
    public GetDrawdownAnalysisQueryValidator()
    {
        RuleFor(q => q.Symbol).MustBeSymbol();
        RuleFor(q => q.RunId).MustBeRunId();
        RuleFor(q => q.Top).InclusiveBetween(1, 50);
    }
}

internal sealed class GetDrawdownAnalysisQueryHandler(IDispatcher dispatcher) : IQueryHandler<GetDrawdownAnalysisQuery, DrawdownAnalysis>
{
    public async Task<Result<DrawdownAnalysis>> HandleAsync(GetDrawdownAnalysisQuery query, CancellationToken cancellationToken)
    {
        var report = await dispatcher.SendAsync(new GetBacktestReportQuery(query.Symbol, query.RunId), cancellationToken);
        if (report.IsFailure)
            return report.Error;

        var equity = report.Value.Equity;
        if (equity.Count == 0)
            return AnalyticsErrors.NoEquity(query.RunId);

        return new DrawdownAnalysis(
            Math.Round(PerformanceCalculator.MaxDrawdownPercent(equity), 4),
            TimeUnderwater(equity),
            PerformanceCalculator.DrawdownPeriods(equity, query.Top));
    }

    private static double TimeUnderwater(IReadOnlyList<EquityPoint> equity)
    {
        decimal peak = 0;
        var below = 0;
        foreach (var point in equity)
        {
            if (point.Value >= peak)
                peak = point.Value;
            else
                below++;
        }

        return below * 100.0 / equity.Count;
    }
}
