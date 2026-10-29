using Stockhog.Domain.Common;

namespace Stockhog.Domain.Backtesting;

/// <summary>
/// Strategy inputs as name/value pairs, e.g. <c>fast-period = 26</c>. Names are lower-case kebab-case.
/// </summary>
public sealed class StrategyParameters : IEquatable<StrategyParameters>
{
    private readonly SortedDictionary<string, string> _values;

    private StrategyParameters(SortedDictionary<string, string> values)
    {
        _values = values;
    }

    public static StrategyParameters Empty { get; } = new(new SortedDictionary<string, string>(StringComparer.Ordinal));

    public IReadOnlyDictionary<string, string> Values => _values;

    public static StrategyParameters Create(IEnumerable<KeyValuePair<string, string>> values)
    {
        var copy = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var (name, value) in values)
        {
            if (!IsValidName(name))
                throw new DomainException($"'{name}' is not a valid parameter name.");
            if (string.IsNullOrWhiteSpace(value))
                throw new DomainException($"Parameter '{name}' has no value.");

            copy[name] = value.Trim();
        }

        return new StrategyParameters(copy);
    }

    public static bool IsValidName(string? name) =>
        !string.IsNullOrEmpty(name)
        && char.IsAsciiLetterLower(name[0])
        && name.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '-');

    public bool Equals(StrategyParameters? other) =>
        other is not null && _values.SequenceEqual(other._values);

    public override bool Equals(object? obj) => obj is StrategyParameters other && Equals(other);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var (name, value) in _values)
        {
            hash.Add(name);
            hash.Add(value);
        }

        return hash.ToHashCode();
    }

    public override string ToString() => string.Join(", ", _values.Select(p => $"{p.Key}={p.Value}"));
}
