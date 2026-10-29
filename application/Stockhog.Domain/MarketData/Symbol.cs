using Stockhog.Domain.Common;

namespace Stockhog.Domain.MarketData;

/// <summary>
/// Exchange ticker, e.g. AAPL or BRK.B. Always upper case.
/// </summary>
public sealed record Symbol
{
    public const int MaxLength = 10;

    private Symbol(string value)
    {
        Value = value;
    }

    public string Value { get; }

    public static bool IsValid(string? value)
    {
        var normalized = Normalize(value);
        return normalized.Length is > 0 and <= MaxLength
               && char.IsAsciiLetter(normalized[0])
               && normalized.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-');
    }

    public static Symbol Create(string value) =>
        IsValid(value) ? new Symbol(Normalize(value)) : throw new DomainException($"'{value}' is not a valid symbol.");

    public override string ToString() => Value;

    private static string Normalize(string? value) => (value ?? string.Empty).Trim().ToUpperInvariant();
}
