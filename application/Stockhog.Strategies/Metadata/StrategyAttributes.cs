namespace Stockhog.Strategies.Metadata;

/// <summary>
/// Marks an algorithm as selectable in the app.
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class StrategyAttribute(string displayName) : Attribute
{
    public string DisplayName { get; } = displayName;

    public string? Description { get; set; }
}

public enum StrategyParameterType
{
    Integer,
    Decimal
}

/// <summary>
/// Describes one input of a strategy. The app builds its form and validation from these, and
/// <see cref="StrategyBase"/> reads the default from here, so each default exists in one place.
/// </summary>
/// <remarks>Attribute arguments cannot be <c>decimal</c>, so numbers are declared as <c>double</c>.</remarks>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = true, Inherited = false)]
public sealed class StrategyParameterAttribute(string name, string displayName, StrategyParameterType type, double defaultValue) : Attribute
{
    /// <summary>Lower-case kebab-case, e.g. <c>fast-period</c>.</summary>
    public string Name { get; } = name;

    public string DisplayName { get; } = displayName;

    public StrategyParameterType Type { get; } = type;

    public double DefaultValue { get; } = defaultValue;

    /// <summary>Inclusive; <see cref="double.NaN"/> means no limit.</summary>
    public double Minimum { get; set; } = double.NaN;

    /// <summary>Inclusive; <see cref="double.NaN"/> means no limit.</summary>
    public double Maximum { get; set; } = double.NaN;

    /// <summary>Name of another parameter this one must be strictly greater than, e.g. slow period above fast period.</summary>
    public string? MustExceed { get; set; }

    public string? Description { get; set; }

    /// <summary>
    /// Start of the range worth testing in an optimization; with <see cref="SuggestedTo"/> and <see cref="SuggestedStep"/>
    /// it pre-fills the optimization form. <see cref="double.NaN"/> when there is no suggestion.
    /// </summary>
    public double SuggestedFrom { get; set; } = double.NaN;

    public double SuggestedTo { get; set; } = double.NaN;

    public double SuggestedStep { get; set; } = double.NaN;
}
