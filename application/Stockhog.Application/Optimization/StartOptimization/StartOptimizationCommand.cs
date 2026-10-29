using Stockhog.Application.Abstractions.Jobs;
using Stockhog.Application.Abstractions.Messaging;
using Stockhog.Application.Abstractions.Persistence;
using Stockhog.Application.Abstractions.Results;
using Stockhog.Application.Backtesting.Strategies;
using Stockhog.Application.Common;
using Stockhog.Application.Optimization.ExecuteOptimization;
using Stockhog.Domain.Backtesting;
using Stockhog.Domain.Common;
using Stockhog.Domain.MarketData;
using Stockhog.Domain.Optimization;
using FluentValidation;

namespace Stockhog.Application.Optimization.StartOptimization;

/// <summary>
/// Creates a grid-search optimization and queues it as one background job, which runs its trials
/// <paramref name="MaxParallelTrials"/> engine processes at a time.
/// </summary>
/// <param name="FixedParameters">Values of the parameters that are not varied; missing ones use their defaults.</param>
/// <param name="Ranges">The varied parameters (1 to 3).</param>
public sealed record StartOptimizationCommand(
    string StrategyName,
    string Symbol,
    DateOnly Start,
    DateOnly End,
    decimal InitialCapital,
    IReadOnlyDictionary<string, string> FixedParameters,
    IReadOnlyList<ParameterRangeInput> Ranges,
    OptimizationObjective Objective,
    int MaxParallelTrials) : ICommand<OptimizationStarted>;

public sealed record ParameterRangeInput(string Name, decimal From, decimal To, decimal Step)
{
    /// <summary>Number of values, or 0 when the range is invalid.</summary>
    public int Count => Step > 0 && To >= From ? (int)decimal.Floor((To - From) / Step) + 1 : 0;
}

/// <param name="SkippedCombinations">Grid points the strategy rejects, e.g. a slow period not above the fast one.</param>
public sealed record OptimizationStarted(string OptimizationId, Guid JobId, int Trials, int SkippedCombinations);

internal sealed class StartOptimizationCommandValidator : AbstractValidator<StartOptimizationCommand>
{
    public StartOptimizationCommandValidator(TimeProvider clock, IStrategyCatalog strategies)
    {
        RuleFor(c => c.StrategyName)
            .MustBeStrategyName()
            .Must(name => strategies.Find(name) is not null).WithMessage("Unknown strategy '{PropertyValue}'.");
        RuleFor(c => c.Symbol).MustBeSymbol();
        RuleFor(c => c.End)
            .GreaterThanOrEqualTo(c => c.Start).WithMessage("End date must not be before start date.")
            .MustNotBeInTheFuture(clock);
        RuleFor(c => c.InitialCapital).GreaterThan(0).WithMessage("Initial capital must be positive.");
        RuleFor(c => c.Objective).IsInEnum();
        RuleFor(c => c.MaxParallelTrials).InclusiveBetween(1, OptimizationRun.MaxParallelTrialsLimit)
            .WithMessage($"Between 1 and {OptimizationRun.MaxParallelTrialsLimit} trials can run in parallel.");
        RuleFor(c => c.Ranges)
            .Must(r => r.Count is >= 1 and <= ParameterGrid.MaxDimensions)
            .WithMessage($"Vary between 1 and {ParameterGrid.MaxDimensions} parameters.")
            .Must(r => r.Select(x => x.Name).Distinct().Count() == r.Count)
            .WithMessage("Each parameter can be varied only once.");

        RuleFor(c => c).Custom((command, context) =>
        {
            if (strategies.Find(command.StrategyName) is not { } strategy)
                return;

            foreach (var error in ParameterErrors(command, strategy))
                context.AddFailure(nameof(StartOptimizationCommand.Ranges), error);
        });
    }

    private static IEnumerable<string> ParameterErrors(StartOptimizationCommand command, StrategyDescriptor strategy)
    {
        foreach (var (name, value) in command.FixedParameters)
        {
            if (strategy.Parameters.FirstOrDefault(p => p.Name == name) is not { } parameter)
                yield return $"{strategy.DisplayName} has no parameter '{name}'.";
            else if (command.Ranges.Any(r => r.Name == name))
                yield return $"{parameter.DisplayName} is varied, so it cannot also have a fixed value.";
            else if (!parameter.TryParse(value, out _, out var error))
                yield return error;
        }

        foreach (var range in command.Ranges)
        {
            if (strategy.Parameters.FirstOrDefault(p => p.Name == range.Name) is not { } parameter)
            {
                yield return $"{strategy.DisplayName} has no parameter '{range.Name}'.";
                continue;
            }

            if (range.Step <= 0)
                yield return $"{parameter.DisplayName}: the step must be positive.";
            else if (range.To < range.From)
                yield return $"{parameter.DisplayName}: 'to' must not be below 'from'.";
            else if (range.Count > ParameterRange.MaxValues)
                yield return $"{parameter.DisplayName} would take {range.Count} values; at most {ParameterRange.MaxValues} are allowed.";

            if (parameter.Minimum is { } min && range.From < min)
                yield return $"{parameter.DisplayName} must be at least {parameter.FormatValue(min)}.";
            if (parameter.Maximum is { } max && range.To > max)
                yield return $"{parameter.DisplayName} must be at most {parameter.FormatValue(max)}.";

            if (parameter.Kind == StrategyParameterKind.Integer
                && new[] { range.From, range.To, range.Step }.Any(v => v != decimal.Truncate(v)))
                yield return $"{parameter.DisplayName}: from, to and step must be whole numbers.";
            else if (new[] { range.From, range.To, range.Step }.Any(v => v != decimal.Round(v, 4)))
                yield return $"{parameter.DisplayName}: use at most 4 decimal places.";
        }

        var combinations = command.Ranges.Aggregate(1L, (total, range) => total * Math.Max(range.Count, 1));
        if (combinations > ParameterGrid.MaxCombinations)
            yield return $"The grid has {combinations} combinations; at most {ParameterGrid.MaxCombinations} are allowed.";
    }
}

internal sealed class StartOptimizationCommandHandler(
    IOptimizationRunRepository repository,
    IUnitOfWork unitOfWork,
    IStrategyCatalog strategies,
    IJobQueue jobs,
    TimeProvider clock) : ICommandHandler<StartOptimizationCommand, OptimizationStarted>
{
    public async Task<Result<OptimizationStarted>> HandleAsync(StartOptimizationCommand command, CancellationToken cancellationToken)
    {
        var symbol = Symbol.Create(command.Symbol);
        var strategyName = StrategyName.Create(command.StrategyName);
        var strategy = strategies.Find(strategyName.Value)!;
        var grid = ParameterGrid.Create(command.Ranges.Select(r => ParameterRange.Create(r.Name, r.From, r.To, r.Step)));

        // Each grid point plus the fixed values must form a valid parameter set; points the strategy rejects
        // (e.g. slow period <= fast period) are skipped instead of failing the whole optimization.
        var trials = new List<StrategyParameters>();
        string? firstRejection = null;
        foreach (var point in grid.Expand())
        {
            var values = new Dictionary<string, string>(command.FixedParameters);
            foreach (var (name, value) in point)
                values[name] = strategy.Parameters.Single(p => p.Name == name).FormatValue(value);

            var errors = strategy.Validate(values);
            if (errors.Count == 0)
                trials.Add(StrategyParameters.Create(strategy.WithDefaults(values)));
            else
                firstRejection ??= errors[0];
        }

        if (trials.Count == 0)
            return OptimizationErrors.NoValidCombination(firstRejection ?? "the grid is empty.");

        var run = OptimizationRun.Create(
            OptimizationRunId.New(symbol, strategyName, clock.GetLocalNow()),
            symbol,
            strategyName,
            DateRange.Create(command.Start, command.End),
            Money.Usd(command.InitialCapital),
            grid,
            command.Objective,
            command.MaxParallelTrials,
            trials,
            clock.GetUtcNow());

        // Saved before queuing: the job loads the optimization from storage and may start right away.
        repository.Add(run);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        var id = run.Id.Value;
        var job = jobs.Enqueue(
            $"Optimization {strategyName.Value} {symbol.Value} ({trials.Count} trials)",
            context => ExecuteAsync(id, context),
            $"optimization:{id}");

        return new OptimizationStarted(id, job.Job.Id, trials.Count, grid.Combinations - trials.Count);
    }

    private static async Task<JobOutcome> ExecuteAsync(string optimizationId, JobExecutionContext context)
    {
        var command = new ExecuteOptimizationCommand(optimizationId)
        {
            Progress = new DelegateProgress<OptimizationProgress>(p =>
                context.Progress.Report(new JobProgress(p.Percent, $"{p.FinishedTrials}/{p.TotalTrials} trials")))
        };

        var result = await context.Dispatcher.SendAsync(command, context.CancellationToken);
        return result.Match(
            outcome => outcome.Status == OptimizationStatus.Completed
                ? JobOutcome.Succeeded($"Best trial #{outcome.BestTrialIndex}: {outcome.BestScore:0.####} ({outcome.CompletedTrials}/{outcome.TotalTrials} completed).")
                : JobOutcome.Failed(outcome.FailureReason ?? outcome.Status.ToString()),
            error => JobOutcome.Failed(error.Message));
    }
}
