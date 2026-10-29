using System.Globalization;
using System.Windows.Data;

namespace Stockhog.Presentation.Localization;

/// <summary>
/// Translates a resource key that comes from data (e.g. a menu item's TitleKey). Use in a MultiBinding whose second
/// binding is <see cref="Loc.Culture"/>, so the text is re-evaluated when the language changes.
/// </summary>
public sealed class LocalizeKeyConverter : IMultiValueConverter
{
    public object Convert(object?[] values, Type targetType, object? parameter, CultureInfo culture) =>
        values.Length > 0 && values[0] is string key ? Loc.Get(key) : string.Empty;

    public object[] ConvertBack(object? value, Type[] targetTypes, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
