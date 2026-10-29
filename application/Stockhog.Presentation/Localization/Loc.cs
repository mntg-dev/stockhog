using System.ComponentModel;
using System.Globalization;
using System.Resources;
using System.Windows.Data;
using System.Windows.Markup;

namespace Stockhog.Presentation.Localization;

/// <summary>
/// UI texts from Resources/Strings*.resx. XAML binds to the indexer (see <see cref="TrExtension"/>), so changing
/// <see cref="Culture"/> refreshes every label at runtime without reopening windows.
/// </summary>
public sealed class Loc : INotifyPropertyChanged
{
    public static readonly CultureInfo Polish = CultureInfo.GetCultureInfo("pl-PL");
    public static readonly CultureInfo English = CultureInfo.GetCultureInfo("en-US");
    public static readonly IReadOnlyList<CultureInfo> SupportedCultures = [Polish, English];

    private static readonly ResourceManager Resources = new("Stockhog.Presentation.Resources.Strings", typeof(Loc).Assembly);

    private CultureInfo _culture = Polish;

    private Loc()
    {
    }

    public static Loc Instance { get; } = new();

    public event PropertyChangedEventHandler? PropertyChanged;

    public CultureInfo Culture => _culture;

    /// <summary>For WPF elements' Language property, which drives StringFormat number and date formatting.</summary>
    public XmlLanguage XmlLanguage => XmlLanguage.GetLanguage(_culture.IetfLanguageTag);

    /// <summary>The text for <paramref name="key"/>, or <c>[key]</c> so missing translations are easy to spot.</summary>
    public string this[string key] => Resources.GetString(key, _culture) ?? $"[{key}]";

    public static string Get(string key) => Instance[key];

    public static bool TryGet(string key, out string text)
    {
        text = Resources.GetString(key, Instance._culture) ?? string.Empty;
        return text.Length > 0;
    }

    public static string Format(string key, params object?[] args) => string.Format(Instance._culture, Instance[key], args);

    public void SetCulture(CultureInfo culture)
    {
        if (Equals(culture, _culture))
            return;

        _culture = culture;
        CultureInfo.CurrentCulture = CultureInfo.CurrentUICulture = culture;
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.DefaultThreadCurrentUICulture = culture;

        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(Binding.IndexerName));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Culture)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(XmlLanguage)));
    }
}

/// <summary>
/// <c>Text="{loc:Tr Nav_Backtest}"</c>: a one-way binding to <see cref="Loc"/>'s indexer that follows language changes.
/// Works on non-visual objects too, e.g. DataGrid column headers, because the binding has an explicit source.
/// </summary>
[MarkupExtensionReturnType(typeof(object))]
public sealed class TrExtension(string key) : MarkupExtension
{
    public string Key { get; set; } = key;

    public override object ProvideValue(IServiceProvider serviceProvider) =>
        new Binding($"[{Key}]") { Source = Loc.Instance, Mode = BindingMode.OneWay }.ProvideValue(serviceProvider);
}
