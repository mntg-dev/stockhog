using System.Reflection;
using Stockhog.Application.Backtesting.Strategies;
using Stockhog.Strategies;
using Stockhog.Strategies.Metadata;
using QuantConnect.Algorithm;

namespace Stockhog.Infrastructure.Lean.Strategies;

/// <summary>
/// Discovers strategies by reflection over the strategies assembly: every algorithm marked with
/// <see cref="StrategyAttribute"/>. The class name is what the engine runner loads.
/// </summary>
internal sealed class LeanStrategyCatalog : IStrategyCatalog
{
    private readonly Lazy<IReadOnlyList<StrategyDescriptor>> _strategies = new(() => Discover(typeof(StrategyBase).Assembly));

    public IReadOnlyList<StrategyDescriptor> GetAll() => _strategies.Value;

    public StrategyDescriptor? Find(string name) => GetAll().FirstOrDefault(s => s.Name == name);

    internal static IReadOnlyList<StrategyDescriptor> Discover(Assembly assembly) =>
        assembly.GetTypes()
            .Where(type => type is { IsClass: true, IsAbstract: false } && typeof(QCAlgorithm).IsAssignableFrom(type))
            .Select(type => (Type: type, Info: type.GetCustomAttribute<StrategyAttribute>()))
            .Where(x => x.Info is not null)
            .Select(x => new StrategyDescriptor(
                x.Type.Name,
                x.Info!.DisplayName,
                x.Info.Description,
                x.Type.GetCustomAttributes<StrategyParameterAttribute>().Select(ToDescriptor).ToList()))
            .OrderBy(s => s.DisplayName, StringComparer.Ordinal)
            .ToList();

    private static StrategyParameterDescriptor ToDescriptor(StrategyParameterAttribute parameter) => new(
        parameter.Name,
        parameter.DisplayName,
        parameter.Type == StrategyParameterType.Integer ? StrategyParameterKind.Integer : StrategyParameterKind.Decimal,
        (decimal)parameter.DefaultValue,
        double.IsNaN(parameter.Minimum) ? null : (decimal)parameter.Minimum,
        double.IsNaN(parameter.Maximum) ? null : (decimal)parameter.Maximum,
        parameter.MustExceed,
        parameter.Description,
        double.IsNaN(parameter.SuggestedFrom) || double.IsNaN(parameter.SuggestedTo) || double.IsNaN(parameter.SuggestedStep)
            ? null
            : new SuggestedRange((decimal)parameter.SuggestedFrom, (decimal)parameter.SuggestedTo, (decimal)parameter.SuggestedStep));
}
