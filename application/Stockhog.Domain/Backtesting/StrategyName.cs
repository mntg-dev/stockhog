using Stockhog.Domain.Common;

namespace Stockhog.Domain.Backtesting;

/// <summary>
/// Name of the algorithm class the engine runs, e.g. SmaCrossStrategy.
/// </summary>
public sealed record StrategyName
{
    public const int MaxLength = 64;

    private StrategyName(string value)
    {
        Value = value;
    }

    public string Value { get; }

    public static bool IsValid(string? value) =>
        !string.IsNullOrEmpty(value)
        && value.Length <= MaxLength
        && char.IsAsciiLetter(value[0])
        && value.All(char.IsAsciiLetterOrDigit);

    public static StrategyName Create(string value) =>
        IsValid(value) ? new StrategyName(value) : throw new DomainException($"'{value}' is not a valid strategy name.");

    public override string ToString() => Value;
}
