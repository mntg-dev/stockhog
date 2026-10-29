using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Stockhog.Application.Backtesting.Strategies;
using Stockhog.Application.Optimization.StartOptimization;
using Stockhog.Domain.Optimization;
using Stockhog.Presentation.Localization;
using Stockhog.Presentation.Strategies;

namespace Stockhog.Presentation.Optimization;

/// <summary>
/// One strategy parameter on the optimization form: either a fixed value or a range (from, to, step) to vary.
/// </summary>
public sealed partial class OptimizationParameterInput : ObservableObject
{
    private const int DefaultValues = 5;

    private readonly string _strategyName;
    private readonly StrategyParameterDescriptor _descriptor;

    public OptimizationParameterInput(string strategyName, StrategyParameterDescriptor descriptor)
    {
        _strategyName = strategyName;
        _descriptor = descriptor;
        _value = descriptor.FormatValue(descriptor.DefaultValue);

        // The strategy's own suggestion when it has one; otherwise a neighbourhood of the default.
        if (descriptor.Suggested is { } suggested)
        {
            _from = descriptor.FormatValue(suggested.From);
            _to = descriptor.FormatValue(suggested.To);
            _step = descriptor.FormatValue(suggested.Step);
            return;
        }

        var step = DefaultStep(descriptor);
        var to = descriptor.DefaultValue + step * (DefaultValues - 1);
        if (descriptor.Maximum is { } max && to > max)
            to = max;

        _from = descriptor.FormatValue(descriptor.DefaultValue);
        _to = descriptor.FormatValue(to);
        _step = descriptor.FormatValue(step);
    }

    public string Name => _descriptor.Name;

    /// <summary>The English name, which stays the same in every language, for UI automation.</summary>
    public string AutomationName => _descriptor.DisplayName;

    public string DisplayName => StrategyTexts.ParameterName(_strategyName, _descriptor);

    public string? Description => StrategyTexts.ParameterDescription(_strategyName, _descriptor);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Error), nameof(Count))]
    private bool _isVaried;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Error))]
    private string _value;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Error), nameof(Count))]
    private string _from;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Error), nameof(Count))]
    private string _to;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Error), nameof(Count))]
    private string _step;

    /// <summary>How many values the range takes; 1 for a fixed value, 0 while the range is invalid.</summary>
    public int Count => !IsVaried ? 1 : TryGetRange(out var range) ? range.Count : 0;

    /// <summary>Validation message for this row alone, or <c>null</c>.</summary>
    public string? Error
    {
        get
        {
            if (!IsVaried)
                return _descriptor.TryParse(Value, out _, out var error) ? null : error;

            if (!_descriptor.TryParse(From, out var from, out var fromError))
                return fromError;
            if (!_descriptor.TryParse(To, out var to, out var toError))
                return toError;
            if (!TryParseNumber(Step, out var step) || step <= 0)
                return Loc.Get("Optimization_StepPositive");
            if (to < from)
                return Loc.Get("Optimization_ToBelowFrom");
            if (_descriptor.Kind == StrategyParameterKind.Integer && step != decimal.Truncate(step))
                return Loc.Get("Optimization_WholeStep");

            var count = new ParameterRangeInput(Name, from, to, step).Count;
            return count > ParameterRange.MaxValues ? Loc.Format("Optimization_TooManyValues", count, ParameterRange.MaxValues) : null;
        }
    }

    public bool TryGetRange(out ParameterRangeInput range)
    {
        range = default!;
        if (!IsVaried || Error is not null)
            return false;

        TryParseNumber(From, out var from);
        TryParseNumber(To, out var to);
        TryParseNumber(Step, out var step);
        range = new ParameterRangeInput(Name, from, to, step);
        return true;
    }

    private static bool TryParseNumber(string text, out decimal value) =>
        decimal.TryParse(text.Trim().Replace(',', '.'), NumberStyles.Number, CultureInfo.InvariantCulture, out value);

    /// <summary>About a fifth of the default value: five steps span a sensible neighbourhood of the default.</summary>
    private static decimal DefaultStep(StrategyParameterDescriptor descriptor)
    {
        var step = Math.Abs(descriptor.DefaultValue) / DefaultValues;
        return descriptor.Kind == StrategyParameterKind.Integer
            ? Math.Max(1, Math.Round(step))
            : step == 0 ? 0.1m : Math.Max(Math.Round(step, 4, MidpointRounding.AwayFromZero), 0.0001m);
    }
}
