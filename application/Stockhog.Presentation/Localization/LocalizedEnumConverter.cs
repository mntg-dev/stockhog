using System.Globalization;
using System.Windows.Data;

namespace Stockhog.Presentation.Localization;

/// <summary>
/// Shows an enum value in the current language using the resource key <c>{EnumType}_{Value}</c>,
/// e.g. <c>JobStatus_Running</c>; falls back to the value's name.
/// </summary>
public sealed class LocalizedEnumConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is Enum e && Loc.TryGet($"{e.GetType().Name}_{e}", out var text) ? text : value?.ToString();

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
