using Stockhog.Application.Backtesting.Strategies;
using Stockhog.Presentation.Localization;

namespace Stockhog.Presentation.Strategies;

/// <summary>
/// Strategy and parameter texts in the current language, from the resources <c>Strategy_{Class}_{Name|Description}</c>
/// and <c>StrategyParam_{Class}_{parameter}_{Name|Description}</c>. The English resources repeat the strategy
/// attributes (a test keeps them equal); the attributes are the fallback for a strategy without resources.
/// </summary>
public static class StrategyTexts
{
    public static string Name(StrategyDescriptor strategy) => Translate($"Strategy_{strategy.Name}_Name") ?? strategy.DisplayName;

    /// <summary>The display name for a strategy class name, or the class name for a strategy the app doesn't know.</summary>
    public static string Name(string strategyName) => Translate($"Strategy_{strategyName}_Name") ?? strategyName;

    public static string? Description(StrategyDescriptor strategy) => Translate($"Strategy_{strategy.Name}_Description") ?? strategy.Description;

    public static string ParameterName(string strategyName, StrategyParameterDescriptor parameter) =>
        ParameterName(strategyName, parameter.Name, parameter.DisplayName);

    /// <param name="fallback">Shown for a parameter without resources, usually its attribute name.</param>
    public static string ParameterName(string strategyName, string parameterName, string fallback) =>
        Translate($"StrategyParam_{strategyName}_{parameterName}_Name") ?? fallback;

    public static string? ParameterDescription(string strategyName, StrategyParameterDescriptor parameter) =>
        Translate($"StrategyParam_{strategyName}_{parameter.Name}_Description") ?? parameter.Description;

    private static string? Translate(string key) => Loc.TryGet(key, out var text) ? text : null;
}
