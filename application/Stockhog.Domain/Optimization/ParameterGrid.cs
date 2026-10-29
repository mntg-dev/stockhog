using Stockhog.Domain.Backtesting;
using Stockhog.Domain.Common;

namespace Stockhog.Domain.Optimization;

/// <summary>
/// The values one strategy parameter takes during an optimization: <c>From</c>, <c>From + Step</c>, … up to <c>To</c>.
/// </summary>
public sealed record ParameterRange
{
    public const int MaxValues = 50;

    private ParameterRange(string name, decimal from, decimal to, decimal step)
    {
        Name = name;
        From = from;
        To = to;
        Step = step;
    }

    public string Name { get; }
    public decimal From { get; }
    public decimal To { get; }
    public decimal Step { get; }

    public int Count => (int)decimal.Floor((To - From) / Step) + 1;

    public static ParameterRange Create(string name, decimal from, decimal to, decimal step)
    {
        if (!StrategyParameters.IsValidName(name))
            throw new DomainException($"'{name}' is not a valid parameter name.");
        if (step <= 0)
            throw new DomainException($"The step of '{name}' must be positive.");
        if (to < from)
            throw new DomainException($"The range of '{name}' ends before it starts.");

        var range = new ParameterRange(name, from, to, step);
        return range.Count <= MaxValues
            ? range
            : throw new DomainException($"'{name}' would take {range.Count} values; at most {MaxValues} are allowed.");
    }

    public IEnumerable<decimal> Values()
    {
        for (var i = 0; i < Count; i++)
            yield return From + i * Step;
    }
}

/// <summary>
/// The parameter space of an optimization: every combination of the values of up to <see cref="MaxDimensions"/> ranges.
/// </summary>
public sealed class ParameterGrid : IEquatable<ParameterGrid>
{
    public const int MaxDimensions = 3;
    public const int MaxCombinations = 400;

    private ParameterGrid(IReadOnlyList<ParameterRange> ranges)
    {
        Ranges = ranges;
    }

    public IReadOnlyList<ParameterRange> Ranges { get; }

    public int Combinations => Ranges.Aggregate(1, (total, range) => total * range.Count);

    public static ParameterGrid Create(IEnumerable<ParameterRange> ranges)
    {
        var list = ranges.ToList();
        if (list.Count is 0 or > MaxDimensions)
            throw new DomainException($"An optimization varies between 1 and {MaxDimensions} parameters.");
        if (list.Select(r => r.Name).Distinct(StringComparer.Ordinal).Count() != list.Count)
            throw new DomainException("Each parameter can be varied only once.");

        var grid = new ParameterGrid(list.AsReadOnly());
        return grid.Combinations <= MaxCombinations
            ? grid
            : throw new DomainException($"The grid has {grid.Combinations} combinations; at most {MaxCombinations} are allowed.");
    }

    /// <summary>Every combination, the first range varying slowest.</summary>
    public IEnumerable<IReadOnlyDictionary<string, decimal>> Expand()
    {
        IEnumerable<IReadOnlyDictionary<string, decimal>> points = [new Dictionary<string, decimal>(StringComparer.Ordinal)];
        foreach (var range in Ranges)
        {
            var current = range;
            points = points.SelectMany(point => current.Values().Select(value =>
                (IReadOnlyDictionary<string, decimal>)new Dictionary<string, decimal>(point, StringComparer.Ordinal) { [current.Name] = value }));
        }

        return points;
    }

    public bool Equals(ParameterGrid? other) => other is not null && Ranges.SequenceEqual(other.Ranges);

    public override bool Equals(object? obj) => obj is ParameterGrid other && Equals(other);

    public override int GetHashCode() => Ranges.Aggregate(0, HashCode.Combine);

    public override string ToString() => string.Join(", ", Ranges.Select(r => $"{r.Name}={r.From}..{r.To}/{r.Step}"));
}
