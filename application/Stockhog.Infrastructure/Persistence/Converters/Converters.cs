using System.Text.Json;
using Stockhog.Domain.Backtesting;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Stockhog.Infrastructure.Persistence.Converters;

internal sealed class UtcDateTimeOffsetConverter() : ValueConverter<DateTimeOffset, DateTime>(
    value => value.UtcDateTime,
    value => new DateTimeOffset(DateTime.SpecifyKind(value, DateTimeKind.Utc)));

/// <summary>
/// Stores strategy parameters as a JSON object, e.g. <c>{"fast-period":"26","slow-period":"130"}</c>.
/// </summary>
internal sealed class StrategyParametersConverter() : ValueConverter<StrategyParameters, string>(
    parameters => ToJson(parameters),
    json => FromJson(json))
{
    /// <summary>The value object is immutable, so the snapshot can be the same instance.</summary>
    public static readonly ValueComparer<StrategyParameters> Comparer = new(
        (left, right) => Equals(left, right),
        parameters => parameters.GetHashCode(),
        parameters => parameters);

    private static string ToJson(StrategyParameters parameters) => JsonSerializer.Serialize(parameters.Values);

    private static StrategyParameters FromJson(string json) =>
        StrategyParameters.Create(JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? []);
}

/// <summary>
/// Stores metrics as one JSON column. Ranking happens in memory: SQLite cannot compare decimals in SQL anyway.
/// Metrics are an immutable record, so the default (value equality) comparer works.
/// </summary>
internal sealed class PerformanceMetricsConverter() : ValueConverter<PerformanceMetrics?, string?>(
    metrics => metrics == null ? null : JsonSerializer.Serialize(metrics, JsonOptions),
    json => json == null ? null : JsonSerializer.Deserialize<PerformanceMetrics>(json, JsonOptions))
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
}
