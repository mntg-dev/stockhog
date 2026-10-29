using CommunityToolkit.Mvvm.ComponentModel;
using Stockhog.Application.Backtesting.Strategies;
using Stockhog.Presentation.Localization;
using Stockhog.Presentation.Strategies;

namespace Stockhog.Presentation.Backtesting;

/// <summary>
/// One field of the strategy form, generated from the strategy's parameter declaration.
/// </summary>
public sealed partial class StrategyParameterInput : ObservableObject
{
    private readonly string _strategyName;
    private readonly StrategyParameterDescriptor _descriptor;

    public StrategyParameterInput(string strategyName, StrategyParameterDescriptor descriptor)
    {
        _strategyName = strategyName;
        _descriptor = descriptor;
        _value = descriptor.FormatValue(descriptor.DefaultValue);
    }

    public string Name => _descriptor.Name;

    public string DisplayName => StrategyTexts.ParameterName(_strategyName, _descriptor);

    /// <summary>Description plus the allowed range, shown as a tooltip.</summary>
    public string Hint
    {
        get
        {
            var range = (_descriptor.Minimum, _descriptor.Maximum) switch
            {
                ({ } min, { } max) => $"{_descriptor.FormatValue(min)} – {_descriptor.FormatValue(max)}",
                ({ } min, null) => $"≥ {_descriptor.FormatValue(min)}",
                (null, { } max) => $"≤ {_descriptor.FormatValue(max)}",
                _ => null
            };
            var parts = new[] { StrategyTexts.ParameterDescription(_strategyName, _descriptor), range is null ? null : Loc.Format("Strategy_Range", range) };
            return string.Join(" ", parts.Where(p => p is not null));
        }
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Error))]
    private string _value;

    /// <summary>Validation message for this field alone, or <c>null</c>.</summary>
    public string? Error => _descriptor.TryParse(Value, out _, out var error) ? null : error;
}
