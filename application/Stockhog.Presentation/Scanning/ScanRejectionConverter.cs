using System.Globalization;
using System.Windows.Data;
using Stockhog.Domain.Scanning;
using Stockhog.Presentation.Localization;

namespace Stockhog.Presentation.Scanning;

/// <summary>
/// Lists every failed check of a candidate in the current language, e.g. "Too few trades, Lost money in one half";
/// empty when it passed. <see cref="LocalizedEnumConverter"/> cannot do this: the value is a combination of flags.
/// </summary>
public sealed class ScanRejectionConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is ScanRejection rejections
            ? string.Join(", ", Enum.GetValues<ScanRejection>()
                .Where(flag => flag != ScanRejection.None && rejections.HasFlag(flag))
                .Select(flag => Loc.Get($"ScanRejection_{flag}")))
            : string.Empty;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
