using System.Globalization;
using Stockhog.Application.Backtesting.Strategies;
using Stockhog.Domain.Backtesting;
using Stockhog.Domain.Optimization;

namespace Stockhog.Application.Optimization;

public sealed record OptimizationSummary(
    string Id,
    string Symbol,
    string StrategyName,
    DateOnly Start,
    DateOnly End,
    OptimizationObjective Objective,
    OptimizationStatus Status,
    int TotalTrials,
    int FinishedTrials,
    decimal? BestScore,
    DateTimeOffset CreatedAt,
    TimeSpan? Duration,
    string? FailureReason)
{
    public int Progress => TotalTrials == 0 ? 0 : FinishedTrials * 100 / TotalTrials;

    public bool IsActive => Status is OptimizationStatus.Queued or OptimizationStatus.Running;
}

/// <summary>One varied parameter and the values it takes, formatted like the strategy formats them.</summary>
public sealed record OptimizationAxis(string Name, string DisplayName, IReadOnlyList<decimal> Values, IReadOnlyList<string> Labels);

/// <param name="Parameters">Every parameter of the trial's run.</param>
/// <param name="Point">The varied parameters only.</param>
public sealed record TrialResult(
    int Index,
    IReadOnlyDictionary<string, string> Parameters,
    IReadOnlyDictionary<string, decimal> Point,
    TrialStatus Status,
    string? RunId,
    PerformanceMetrics? Metrics,
    decimal? Score,
    bool IsBest,
    string? FailureReason,
    TrialStatus? CheckStatus = null,
    PerformanceMetrics? CheckMetrics = null,
    decimal? CheckScore = null,
    string? CheckRunId = null)
{
    public string PointText => string.Join(", ", Point.Select(p => $"{p.Key}={p.Value.ToString("0.####", CultureInfo.InvariantCulture)}"));
}

/// <param name="Scores"><c>[y, x]</c>: the score of the trial at those axis values, or <c>null</c> if it has none (yet).
/// When the grid has more axes than shown, a cell holds the best score over the hidden ones.</param>
/// <param name="Best">Cell of the best trial, if there is one.</param>
public sealed record OptimizationHeatmap(OptimizationAxis X, OptimizationAxis? Y, double?[,] Scores, (int X, int Y)? Best);

public sealed record OptimizationDetails(
    OptimizationSummary Summary,
    decimal InitialCapital,
    int MaxParallelTrials,
    IReadOnlyList<OptimizationAxis> Axes,
    IReadOnlyList<TrialResult> Trials,
    DateOnly? CheckStart = null,
    DateOnly? CheckEnd = null)
{
    public bool HasCheck => CheckStart is not null;

    public TrialResult? Best => Trials.FirstOrDefault(t => t.IsBest);

    /// <param name="xAxis">Name of the varied parameter on the horizontal axis.</param>
    /// <param name="yAxis">Name of the one on the vertical axis, or <c>null</c> for a single row.</param>
    public OptimizationHeatmap Heatmap(string xAxis, string? yAxis)
    {
        var x = Axes.Single(a => a.Name == xAxis);
        var y = yAxis is null ? null : Axes.Single(a => a.Name == yAxis);
        var scores = new double?[y?.Values.Count ?? 1, x.Values.Count];
        (int, int)? best = null;

        foreach (var trial in Trials.Where(t => t.Score is not null))
        {
            var column = IndexOf(x, trial.Point[x.Name]);
            var row = y is null ? 0 : IndexOf(y, trial.Point[y.Name]);
            var score = (double)trial.Score!.Value;

            if (scores[row, column] is not { } existing || score > existing)
                scores[row, column] = score;
            if (trial.IsBest)
                best = (column, row);
        }

        return new OptimizationHeatmap(x, y, scores, best);
    }

    private static int IndexOf(OptimizationAxis axis, decimal value)
    {
        for (var i = 0; i < axis.Values.Count; i++)
        {
            if (axis.Values[i] == value)
                return i;
        }

        throw new InvalidOperationException($"{value} is not a value of {axis.Name}.");
    }
}

internal static class OptimizationReadModels
{
    public static OptimizationSummary Summary(OptimizationRun run) => new(
        run.Id.Value,
        run.Symbol.Value,
        run.Strategy.Value,
        run.Period.Start,
        run.Period.End,
        run.Objective,
        run.Status,
        run.Trials.Count,
        run.FinishedTrials,
        run.BestTrial?.Metrics?.Score(run.Objective),
        run.CreatedAt,
        run.Duration,
        run.FailureReason);

    public static OptimizationDetails Details(OptimizationRun run, StrategyDescriptor? strategy)
    {
        var axes = run.Grid.Ranges
            .Select(range =>
            {
                var descriptor = strategy?.Parameters.FirstOrDefault(p => p.Name == range.Name);
                var values = range.Values().ToList();
                return new OptimizationAxis(
                    range.Name,
                    descriptor?.DisplayName ?? range.Name,
                    values,
                    values.Select(v => descriptor?.FormatValue(v) ?? v.ToString(CultureInfo.InvariantCulture)).ToList());
            })
            .ToList();

        var trials = run.Trials
            .Select(trial => new TrialResult(
                trial.Index,
                trial.Parameters.Values,
                axes.ToDictionary(a => a.Name, a => decimal.Parse(trial.Parameters.Values[a.Name], NumberStyles.Number, CultureInfo.InvariantCulture)),
                trial.Status,
                trial.RunId?.Value,
                trial.Metrics,
                trial.Metrics?.Score(run.Objective),
                trial.Index == run.BestTrialIndex,
                trial.FailureReason,
                trial.CheckStatus,
                trial.CheckMetrics,
                trial.CheckMetrics?.Score(run.Objective),
                trial.CheckRunId?.Value))
            .ToList();

        return new OptimizationDetails(
            Summary(run), run.InitialCapital.Amount, run.MaxParallelTrials, axes, trials, run.CheckPeriod?.Start, run.CheckPeriod?.End);
    }
}
