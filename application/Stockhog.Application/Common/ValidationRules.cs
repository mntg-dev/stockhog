using Stockhog.Domain.Backtesting;
using Stockhog.Domain.MarketData;
using FluentValidation;

namespace Stockhog.Application.Common;

/// <summary>
/// Reuses the value objects' own rules so the domain and the validators cannot drift apart.
/// </summary>
internal static class ValidationRules
{
    public static IRuleBuilderOptions<T, string> MustBeSymbol<T>(this IRuleBuilder<T, string> rule) =>
        rule.Must(Symbol.IsValid).WithMessage("'{PropertyValue}' is not a valid symbol (e.g. AAPL).");

    public static IRuleBuilderOptions<T, string> MustBeStrategyName<T>(this IRuleBuilder<T, string> rule) =>
        rule.Must(StrategyName.IsValid).WithMessage("'{PropertyValue}' is not a valid strategy name.");

    public static IRuleBuilderOptions<T, string> MustBeRunId<T>(this IRuleBuilder<T, string> rule) =>
        rule.Must(id => !string.IsNullOrWhiteSpace(id) && id.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.'))
            .WithMessage("'{PropertyValue}' is not a valid run id.");

    public static IRuleBuilderOptions<T, DateOnly> MustNotBeInTheFuture<T>(this IRuleBuilder<T, DateOnly> rule, TimeProvider clock) =>
        rule.Must(day => day <= DateOnly.FromDateTime(clock.GetLocalNow().DateTime))
            .WithMessage("{PropertyName} must not be in the future.");
}
