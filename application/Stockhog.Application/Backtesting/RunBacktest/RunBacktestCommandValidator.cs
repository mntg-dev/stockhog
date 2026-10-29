using Stockhog.Application.Backtesting.Strategies;
using Stockhog.Application.Common;
using Stockhog.Domain.Optimization;
using FluentValidation;

namespace Stockhog.Application.Backtesting.RunBacktest;

internal sealed class RunBacktestCommandValidator : AbstractValidator<RunBacktestCommand>
{
    public RunBacktestCommandValidator(TimeProvider clock, IStrategyCatalog strategies)
    {
        RuleFor(c => c.StrategyName)
            .MustBeStrategyName()
            .Must(name => strategies.Find(name) is not null).WithMessage("Unknown strategy '{PropertyValue}'.");
        RuleFor(c => c.Symbol).MustBeSymbol();
        RuleFor(c => c.End)
            .GreaterThanOrEqualTo(c => c.Start).WithMessage("End date must not be before start date.")
            .MustNotBeInTheFuture(clock);
        RuleFor(c => c.InitialCapital).GreaterThan(0).WithMessage("Initial capital must be positive.");

        RuleFor(c => c.Trial!.OptimizationId)
            .Must(OptimizationRunId.IsValid).WithMessage("'{PropertyValue}' is not a valid optimization id.")
            .When(c => c.Trial is not null);
        RuleFor(c => c.Trial!.Index).GreaterThanOrEqualTo(0).When(c => c.Trial is not null);

        // Ranges, types and relations come from the strategy's own parameter declarations.
        RuleFor(c => c.Parameters).Custom((parameters, context) =>
        {
            if (strategies.Find(context.InstanceToValidate.StrategyName) is not { } strategy)
                return;

            foreach (var error in strategy.Validate(parameters))
                context.AddFailure(nameof(RunBacktestCommand.Parameters), error);
        });
    }
}
