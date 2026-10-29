using System.ComponentModel;
using System.Globalization;
using Stockhog.Application.Abstractions.Messaging;
using Stockhog.Application.Abstractions.Results;

namespace Stockhog.Application.Backtesting.Strategies;

/// <summary>
/// The strategies the engine can run, with their parameters. Fixed while the app runs.
/// </summary>
public interface IStrategyCatalog
{
    IReadOnlyList<StrategyDescriptor> GetAll();

    StrategyDescriptor? Find(string name);
}

[ImmutableObject(true)]
public sealed record StrategyDescriptor(
    string Name,
    string DisplayName,
    string? Description,
    IReadOnlyList<StrategyParameterDescriptor> Parameters)
{
    /// <summary>
    /// Fills in defaults for missing parameters and checks every value against its descriptor.
    /// </summary>
    /// <returns>Messages for each problem; empty when the values are valid.</returns>
    public IReadOnlyList<string> Validate(IReadOnlyDictionary<string, string> values)
    {
        var errors = new List<string>();

        foreach (var unknown in values.Keys.Where(k => Parameters.All(p => p.Name != k)))
            errors.Add($"{DisplayName} has no parameter '{unknown}'.");

        var parsed = new Dictionary<string, decimal>();
        foreach (var parameter in Parameters)
        {
            if (!values.TryGetValue(parameter.Name, out var text))
            {
                parsed[parameter.Name] = parameter.DefaultValue;
                continue;
            }

            if (parameter.TryParse(text, out var value, out var error))
                parsed[parameter.Name] = value;
            else
                errors.Add(error);
        }

        foreach (var parameter in Parameters.Where(p => p.MustExceed is not null))
        {
            var other = Parameters.SingleOrDefault(p => p.Name == parameter.MustExceed);
            if (other is not null
                && parsed.TryGetValue(parameter.Name, out var value)
                && parsed.TryGetValue(other.Name, out var otherValue)
                && value <= otherValue)
            {
                errors.Add($"{parameter.DisplayName} must be greater than {other.DisplayName}.");
            }
        }

        return errors;
    }

    /// <summary>The given values plus defaults for every parameter not given.</summary>
    public IReadOnlyDictionary<string, string> WithDefaults(IReadOnlyDictionary<string, string> values) =>
        Parameters.ToDictionary(
            p => p.Name,
            p => values.TryGetValue(p.Name, out var value) ? value.Trim() : p.FormatValue(p.DefaultValue));
}

public enum StrategyParameterKind
{
    Integer,
    Decimal
}

/// <param name="Suggested">The range worth testing in an optimization, if the strategy suggests one.</param>
[ImmutableObject(true)]
public sealed record StrategyParameterDescriptor(
    string Name,
    string DisplayName,
    StrategyParameterKind Kind,
    decimal DefaultValue,
    decimal? Minimum,
    decimal? Maximum,
    string? MustExceed,
    string? Description,
    SuggestedRange? Suggested = null)
{
    public string FormatValue(decimal value) => value.ToString(Kind == StrategyParameterKind.Integer ? "0" : "0.####", CultureInfo.InvariantCulture);

    /// <summary>Parses an invariant-culture number (a decimal comma is accepted too) and checks type and range.</summary>
    public bool TryParse(string text, out decimal value, out string error)
    {
        error = string.Empty;
        if (!decimal.TryParse(text.Trim().Replace(',', '.'), NumberStyles.Number, CultureInfo.InvariantCulture, out value))
        {
            error = $"{DisplayName}: '{text}' is not a number.";
            return false;
        }

        if (Kind == StrategyParameterKind.Integer && value != decimal.Truncate(value))
            error = $"{DisplayName} must be a whole number.";
        else if (Minimum is { } min && value < min)
            error = $"{DisplayName} must be at least {FormatValue(min)}.";
        else if (Maximum is { } max && value > max)
            error = $"{DisplayName} must be at most {FormatValue(max)}.";

        return error.Length == 0;
    }
}

/// <summary>From, to and step of the values worth testing for one parameter, e.g. 10 to 55 by 5.</summary>
public sealed record SuggestedRange(decimal From, decimal To, decimal Step);

/// <summary>Cached: the catalog cannot change while the app runs.</summary>
public sealed record GetStrategiesQuery : IQuery<IReadOnlyList<StrategyDescriptor>>, ICacheableQuery
{
    public string CacheKey => "strategies";

    public IReadOnlyCollection<string> CacheTags => [];

    public TimeSpan CacheDuration => TimeSpan.FromHours(12);
}

internal sealed class GetStrategiesQueryHandler(IStrategyCatalog catalog) : IQueryHandler<GetStrategiesQuery, IReadOnlyList<StrategyDescriptor>>
{
    public Task<Result<IReadOnlyList<StrategyDescriptor>>> HandleAsync(GetStrategiesQuery query, CancellationToken cancellationToken) =>
        Task.FromResult(Result<IReadOnlyList<StrategyDescriptor>>.Success(catalog.GetAll()));
}
