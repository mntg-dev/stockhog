using Stockhog.Application.Abstractions.Messaging;
using Stockhog.Application.Abstractions.Results;
using Stockhog.Application.Backtesting.Strategies;
using Stockhog.Domain.Optimization;
using FluentValidation;

namespace Stockhog.Application.Optimization;

/// <summary>
/// Recent optimizations, newest first. Not cached: running ones change after every trial.
/// </summary>
public sealed record GetOptimizationsQuery(int Take = 50) : IQuery<IReadOnlyList<OptimizationSummary>>;

internal sealed class GetOptimizationsQueryValidator : AbstractValidator<GetOptimizationsQuery>
{
    public GetOptimizationsQueryValidator() => RuleFor(q => q.Take).InclusiveBetween(1, 500);
}

internal sealed class GetOptimizationsQueryHandler(IOptimizationReader reader)
    : IQueryHandler<GetOptimizationsQuery, IReadOnlyList<OptimizationSummary>>
{
    public async Task<Result<IReadOnlyList<OptimizationSummary>>> HandleAsync(GetOptimizationsQuery query, CancellationToken cancellationToken)
    {
        var runs = await reader.ListAsync(query.Take, cancellationToken);
        return Result<IReadOnlyList<OptimizationSummary>>.Success(runs.Select(OptimizationReadModels.Summary).ToList());
    }
}

/// <summary>One optimization with every trial, for the results table and the heatmap.</summary>
public sealed record GetOptimizationQuery(string OptimizationId) : IQuery<OptimizationDetails>;

internal sealed class GetOptimizationQueryValidator : AbstractValidator<GetOptimizationQuery>
{
    public GetOptimizationQueryValidator() =>
        RuleFor(q => q.OptimizationId).Must(OptimizationRunId.IsValid).WithMessage("'{PropertyValue}' is not a valid optimization id.");
}

internal sealed class GetOptimizationQueryHandler(IOptimizationReader reader, IStrategyCatalog strategies)
    : IQueryHandler<GetOptimizationQuery, OptimizationDetails>
{
    public async Task<Result<OptimizationDetails>> HandleAsync(GetOptimizationQuery query, CancellationToken cancellationToken)
    {
        var run = await reader.FindAsync(OptimizationRunId.From(query.OptimizationId), cancellationToken);
        return run is null
            ? OptimizationErrors.NotFound(query.OptimizationId)
            : OptimizationReadModels.Details(run, strategies.Find(run.Strategy.Value));
    }
}

/// <summary>
/// Sent at startup. An optimization still Queued or Running belongs to a previous session; it is marked as failed,
/// keeping the trials it finished and the best of them.
/// </summary>
/// <returns>The number of optimizations marked as failed.</returns>
public sealed record FailInterruptedOptimizationsCommand : ICommand<int>;

internal sealed class FailInterruptedOptimizationsCommandHandler(IOptimizationRunRepository repository, TimeProvider clock)
    : ICommandHandler<FailInterruptedOptimizationsCommand, int>
{
    public const string Reason = "Interrupted: the application closed before the optimization finished.";

    public async Task<Result<int>> HandleAsync(FailInterruptedOptimizationsCommand command, CancellationToken cancellationToken)
    {
        var unfinished = await repository.ListUnfinishedAsync(cancellationToken);
        foreach (var run in unfinished)
            run.Fail(Reason, clock.GetUtcNow());

        // Saved by UnitOfWorkBehavior.
        return unfinished.Count;
    }
}
