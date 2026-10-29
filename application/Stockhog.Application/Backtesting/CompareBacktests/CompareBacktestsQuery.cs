using Stockhog.Application.Abstractions.Messaging;
using Stockhog.Application.Abstractions.Results;
using Stockhog.Application.Backtesting.GetBacktestReport;
using FluentValidation;

namespace Stockhog.Application.Backtesting.CompareBacktests;

/// <summary>
/// Side-by-side key figures and cumulative returns of several finished runs. Equity is normalized to percent
/// return so runs with different capital, symbols or periods can share one chart.
/// </summary>
public sealed record CompareBacktestsQuery(IReadOnlyList<BacktestRunReference> Runs) : IQuery<BacktestComparison>
{
    public const int MaxRuns = 8;
}

public sealed record BacktestRunReference(string Symbol, string RunId);

public sealed record BacktestComparison(IReadOnlyList<ComparedRun> Runs);

public sealed record ComparedRun(
    string RunId,
    string Symbol,
    string NetProfit,
    string CompoundingAnnualReturn,
    string MaxDrawdown,
    string SharpeRatio,
    string WinRate,
    decimal ProfitFactor,
    int TotalTrades,
    IReadOnlyList<ReturnPoint> CumulativeReturn);

/// <param name="ReturnPercent">Return since the first point, e.g. 12.5 for +12.5%.</param>
public readonly record struct ReturnPoint(DateTime Time, decimal ReturnPercent);

internal sealed class CompareBacktestsQueryValidator : AbstractValidator<CompareBacktestsQuery>
{
    public CompareBacktestsQueryValidator()
    {
        RuleFor(q => q.Runs)
            .Must(r => r.Count is >= 2 and <= CompareBacktestsQuery.MaxRuns)
            .WithMessage($"Select between 2 and {CompareBacktestsQuery.MaxRuns} completed runs to compare.")
            .Must(r => r.Select(x => x.RunId).Distinct().Count() == r.Count)
            .WithMessage("Each run can be compared only once.");
    }
}

internal sealed class CompareBacktestsQueryHandler(IDispatcher dispatcher) : IQueryHandler<CompareBacktestsQuery, BacktestComparison>
{
    public async Task<Result<BacktestComparison>> HandleAsync(CompareBacktestsQuery query, CancellationToken cancellationToken)
    {
        // Reports are read in parallel, each through the cached report query in its own scope.
        var reports = await Task.WhenAll(query.Runs.Select(run =>
            dispatcher.SendAsync(new GetBacktestReportQuery(run.Symbol, run.RunId), cancellationToken)));

        if (reports.FirstOrDefault(r => r.IsFailure) is { } failed)
            return failed.Error!;

        var compared = query.Runs.Zip(reports, (run, report) => Compare(run, report.Value)).ToList();
        return new BacktestComparison(compared);
    }

    private static ComparedRun Compare(BacktestRunReference run, BacktestReport report)
    {
        string Stat(string name) => report.Statistics.GetValueOrDefault(name, "-");

        return new ComparedRun(
            run.RunId,
            run.Symbol.Trim().ToUpperInvariant(),
            Stat("Net Profit"),
            Stat("Compounding Annual Return"),
            Stat("Drawdown"),
            Stat("Sharpe Ratio"),
            Stat("Win Rate"),
            report.TradeStatistics.ProfitFactor,
            report.TradeStatistics.TotalTrades,
            CumulativeReturn(report.Equity));
    }

    internal static IReadOnlyList<ReturnPoint> CumulativeReturn(IReadOnlyList<EquityPoint> equity)
    {
        if (equity.Count == 0 || equity[0].Value == 0)
            return [];

        var start = equity[0].Value;
        return equity.Select(p => new ReturnPoint(p.Time, Math.Round((p.Value / start - 1) * 100, 4))).ToList();
    }
}
