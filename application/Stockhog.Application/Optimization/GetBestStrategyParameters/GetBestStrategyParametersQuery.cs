using Stockhog.Application.Abstractions.Messaging;
using Stockhog.Application.Abstractions.Results;
using Stockhog.Domain.Backtesting;
using Stockhog.Domain.Optimization;
using FluentValidation;

namespace Stockhog.Application.Optimization.GetBestStrategyParameters;

/// <summary>
/// The best trial of each optimization of a strategy, newest first: which parameter values worked best, on which
/// symbol and period, and how well. Optimizations without a scored trial are left out.
/// </summary>
public sealed record GetBestStrategyParametersQuery(string StrategyName, int Take = 50) : IQuery<IReadOnlyList<BestParameters>>
{
    /// <summary>Optimizations looked at; older ones beyond this are ignored.</summary>
    public const int SearchedOptimizations = 500;
}

/// <param name="Parameters">Every parameter of the best trial, as the strategy reads them.</param>
/// <param name="CheckMetrics">How the same parameters did on the check period (days the optimization did not see),
/// if the optimization was checked and this trial was part of it.</param>
public sealed record BestParameters(
    string OptimizationId,
    string Symbol,
    DateOnly Start,
    DateOnly End,
    decimal InitialCapital,
    OptimizationObjective Objective,
    decimal Score,
    PerformanceMetrics Metrics,
    IReadOnlyDictionary<string, string> Parameters,
    DateTimeOffset CreatedAt,
    DateOnly? CheckStart = null,
    DateOnly? CheckEnd = null,
    PerformanceMetrics? CheckMetrics = null,
    decimal? CheckScore = null)
{
    public string ParametersText => string.Join(", ", Parameters.Select(p => $"{p.Key}={p.Value}"));

    public string? CheckPeriodText => CheckStart is { } start && CheckEnd is { } end ? $"{start:yyyy-MM-dd} – {end:yyyy-MM-dd}" : null;
}

internal sealed class GetBestStrategyParametersQueryValidator : AbstractValidator<GetBestStrategyParametersQuery>
{
    public GetBestStrategyParametersQueryValidator()
    {
        RuleFor(q => q.StrategyName).NotEmpty();
        RuleFor(q => q.Take).InclusiveBetween(1, GetBestStrategyParametersQuery.SearchedOptimizations);
    }
}

internal sealed class GetBestStrategyParametersQueryHandler(IOptimizationReader reader)
    : IQueryHandler<GetBestStrategyParametersQuery, IReadOnlyList<BestParameters>>
{
    public async Task<Result<IReadOnlyList<BestParameters>>> HandleAsync(GetBestStrategyParametersQuery query, CancellationToken cancellationToken)
    {
        var runs = await reader.ListAsync(GetBestStrategyParametersQuery.SearchedOptimizations, cancellationToken);

        return runs
            .Where(run => run.Strategy.Value == query.StrategyName && run.BestTrial?.Metrics is not null)
            .Take(query.Take)
            .Select(run =>
            {
                var best = run.BestTrial!;
                return new BestParameters(
                    run.Id.Value,
                    run.Symbol.Value,
                    run.Period.Start,
                    run.Period.End,
                    run.InitialCapital.Amount,
                    run.Objective,
                    best.Metrics!.Score(run.Objective),
                    best.Metrics,
                    best.Parameters.Values,
                    run.CreatedAt,
                    run.CheckPeriod?.Start,
                    run.CheckPeriod?.End,
                    best.CheckMetrics,
                    best.CheckMetrics?.Score(run.Objective));
            })
            .ToList();
    }
}
