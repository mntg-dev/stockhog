using System.Globalization;
using System.Windows.Data;
using Stockhog.Application.Backtesting.Strategies;

namespace Stockhog.Presentation.Strategies;

/// <summary>
/// A strategy's name, or its description with <c>ConverterParameter=Description</c>, in the current language. Accepts
/// a <see cref="StrategyDescriptor"/> or a strategy class name. The shell rebuilds the page when the language changes,
/// which re-runs the conversion.
/// </summary>
public sealed class StrategyTextConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var description = parameter as string == "Description";
        return value switch
        {
            StrategyDescriptor strategy => description ? StrategyTexts.Description(strategy) : StrategyTexts.Name(strategy),
            string name when !description => StrategyTexts.Name(name),
            _ => null
        };
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
