using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace Stockhog.Wpf.Controls;

/// <summary>Finds instruments matching what the user typed, e.g. by symbol prefix or company name.</summary>
public interface ISymbolSuggestionProvider
{
    /// <param name="text">What was typed; empty to browse every symbol the provider offers, in symbol order.</param>
    IReadOnlyList<SymbolSuggestion> Suggest(string text, int maxResults);
}

/// <param name="Detail">Shown dimmed after the name, e.g. the exchange.</param>
public sealed record SymbolSuggestion(string Symbol, string Name, string? Detail = null);

/// <summary>
/// An editable combo box for symbols. Typing suggests matching symbols; the arrow button (or Alt+Down, F4) opens the
/// whole list to browse, filtered by what was typed. Up/Down pick a suggestion, Enter or Tab accepts it, Escape closes
/// the list. With <see cref="AllowMultiple"/> it edits a list such as "AAPL, MSFT": suggestions are for the last entry,
/// and accepting one appends a separator so the next symbol can be typed straight away. Without a
/// <see cref="SuggestionProvider"/> (or before the instrument list is downloaded) it is a plain text box.
/// Lookless: the template is in Themes/Generic.xaml.
/// </summary>
[TemplatePart(Name = TextBoxPart, Type = typeof(TextBox))]
[TemplatePart(Name = PopupPart, Type = typeof(Popup))]
[TemplatePart(Name = ListPart, Type = typeof(ListBox))]
[TemplatePart(Name = DropDownButtonPart, Type = typeof(ButtonBase))]
public sealed class SymbolBox : Control
{
    private const string TextBoxPart = "PART_TextBox";
    private const string PopupPart = "PART_Popup";
    private const string ListPart = "PART_List";
    private const string DropDownButtonPart = "PART_DropDownButton";

    /// <summary>Browsing shows everything; the list is virtualized, so even thousands of rows open at once.</summary>
    private const int MaxBrowseResults = 100_000;

    private static readonly char[] Separators = [',', ';', ' '];

    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text), typeof(string), typeof(SymbolBox),
        new FrameworkPropertyMetadata(string.Empty, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault)
        {
            DefaultUpdateSourceTrigger = System.Windows.Data.UpdateSourceTrigger.PropertyChanged
        });

    public static readonly DependencyProperty SuggestionProviderProperty = DependencyProperty.Register(
        nameof(SuggestionProvider), typeof(ISymbolSuggestionProvider), typeof(SymbolBox));

    public static readonly DependencyProperty AllowMultipleProperty = DependencyProperty.Register(
        nameof(AllowMultiple), typeof(bool), typeof(SymbolBox), new PropertyMetadata(false));

    public static readonly DependencyProperty MaxSuggestionsProperty = DependencyProperty.Register(
        nameof(MaxSuggestions), typeof(int), typeof(SymbolBox), new PropertyMetadata(30));

    private TextBox? _textBox;
    private Popup? _popup;
    private ListBox? _list;
    private ButtonBase? _dropDownButton;
    private bool _accepting;

    static SymbolBox()
    {
        DefaultStyleKeyProperty.OverrideMetadata(typeof(SymbolBox), new FrameworkPropertyMetadata(typeof(SymbolBox)));
        FocusableProperty.OverrideMetadata(typeof(SymbolBox), new FrameworkPropertyMetadata(false));
    }

    public SymbolBox()
    {
        // A popup is its own window: close it when the page goes away, or it would float over the next page.
        Unloaded += (_, _) => ClosePopup();
        IsVisibleChanged += (_, e) =>
        {
            if (e.NewValue is false)
                ClosePopup();
        };
    }

    private void ClosePopup()
    {
        if (_popup is not null)
            _popup.IsOpen = false;
    }

    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public ISymbolSuggestionProvider? SuggestionProvider
    {
        get => (ISymbolSuggestionProvider?)GetValue(SuggestionProviderProperty);
        set => SetValue(SuggestionProviderProperty, value);
    }

    public bool AllowMultiple
    {
        get => (bool)GetValue(AllowMultipleProperty);
        set => SetValue(AllowMultipleProperty, value);
    }

    public int MaxSuggestions
    {
        get => (int)GetValue(MaxSuggestionsProperty);
        set => SetValue(MaxSuggestionsProperty, value);
    }

    public override void OnApplyTemplate()
    {
        if (_textBox is not null)
        {
            _textBox.TextChanged -= OnTextChanged;
            _textBox.PreviewKeyDown -= OnPreviewKeyDown;
            _textBox.LostKeyboardFocus -= OnLostKeyboardFocus;
        }

        if (_list is not null)
            _list.PreviewMouseLeftButtonUp -= OnListMouseUp;

        if (_dropDownButton is not null)
            _dropDownButton.Click -= OnDropDownClick;

        base.OnApplyTemplate();

        _textBox = GetTemplateChild(TextBoxPart) as TextBox;
        _popup = GetTemplateChild(PopupPart) as Popup;
        _list = GetTemplateChild(ListPart) as ListBox;
        _dropDownButton = GetTemplateChild(DropDownButtonPart) as ButtonBase;

        if (_textBox is not null)
        {
            _textBox.TextChanged += OnTextChanged;
            _textBox.PreviewKeyDown += OnPreviewKeyDown;
            _textBox.LostKeyboardFocus += OnLostKeyboardFocus;
        }

        if (_list is not null)
            _list.PreviewMouseLeftButtonUp += OnListMouseUp;

        if (_dropDownButton is not null)
            _dropDownButton.Click += OnDropDownClick;
    }

    private void OnDropDownClick(object sender, RoutedEventArgs e)
    {
        if (_popup is { IsOpen: true })
        {
            _popup.IsOpen = false;
            return;
        }

        // Focus first: the list closes when the text box loses focus.
        _textBox?.Focus();
        Browse();
    }

    /// <summary>Opens the whole list, filtered by the current entry, with the matching symbol selected.</summary>
    private void Browse()
    {
        if (_popup is null || _list is null || _textBox is null || SuggestionProvider is null)
            return;

        var term = CurrentTerm(_textBox.Text);
        var all = SuggestionProvider.Suggest(string.Empty, MaxBrowseResults);
        var match = all.FirstOrDefault(s => string.Equals(s.Symbol, term, StringComparison.OrdinalIgnoreCase));

        // A complete symbol shows the whole list around it; a partial entry narrows the list.
        var items = term.Length == 0 || match is not null ? all : SuggestionProvider.Suggest(term, MaxBrowseResults);

        _list.ItemsSource = items;
        _list.SelectedItem = match ?? items.FirstOrDefault();
        _popup.IsOpen = items.Count > 0;
        if (_list.SelectedItem is { } selected)
            _list.ScrollIntoView(selected);
    }

    /// <summary>Only typing opens suggestions; text set from the view model (or by accepting one) does not.</summary>
    private void OnTextChanged(object sender, TextChangedEventArgs e)
    {
        if (_accepting || _textBox is not { IsKeyboardFocusWithin: true })
            return;

        UpdateSuggestions();
    }

    private void UpdateSuggestions()
    {
        if (_popup is null || _list is null || _textBox is null)
            return;

        var term = CurrentTerm(_textBox.Text);
        var suggestions = term.Length == 0 || SuggestionProvider is null
            ? []
            : SuggestionProvider.Suggest(term, MaxSuggestions);

        // A fully typed symbol that matches only itself needs no list.
        var onlyExactMatch = suggestions.Count == 1 && string.Equals(suggestions[0].Symbol, term, StringComparison.OrdinalIgnoreCase);

        _list.ItemsSource = suggestions;
        _list.SelectedIndex = suggestions.Count > 0 ? 0 : -1;
        _popup.IsOpen = suggestions.Count > 0 && !onlyExactMatch;
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        // Like a ComboBox: Alt+Down or F4 opens the list.
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (_popup is { IsOpen: false } && (key == Key.F4 || key == Key.Down && Keyboard.Modifiers == ModifierKeys.Alt))
        {
            Browse();
            e.Handled = true;
            return;
        }

        if (_popup is not { IsOpen: true } || _list is null)
            return;

        switch (e.Key)
        {
            case Key.Down:
                Move(+1);
                e.Handled = true;
                break;
            case Key.Up:
                Move(-1);
                e.Handled = true;
                break;
            case Key.PageDown:
                Move(+10);
                e.Handled = true;
                break;
            case Key.PageUp:
                Move(-10);
                e.Handled = true;
                break;
            case Key.Enter:
                if (_list.SelectedItem is SymbolSuggestion selected)
                {
                    Accept(selected);
                    e.Handled = true;
                }
                break;
            case Key.Tab:
                // Accept, but let Tab still move focus to the next field.
                if (_list.SelectedItem is SymbolSuggestion tabbed)
                    Accept(tabbed);
                break;
            case Key.Escape:
                _popup.IsOpen = false;
                e.Handled = true;
                break;
        }
    }

    private void Move(int delta)
    {
        if (_list is null || _list.Items.Count == 0)
            return;

        _list.SelectedIndex = Math.Clamp(_list.SelectedIndex + delta, 0, _list.Items.Count - 1);
        _list.ScrollIntoView(_list.SelectedItem);
    }

    /// <summary>The list items are not focusable, so a click keeps focus in the text box; the item is found from the click target.</summary>
    private void OnListMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_list is not null
            && e.OriginalSource is DependencyObject source
            && ItemsControl.ContainerFromElement(_list, source) is ListBoxItem { DataContext: SymbolSuggestion clicked })
        {
            Accept(clicked);
            e.Handled = true;
        }
    }

    private void OnLostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (_popup is not null)
            _popup.IsOpen = false;
    }

    private void Accept(SymbolSuggestion suggestion)
    {
        if (_textBox is null || _popup is null)
            return;

        _accepting = true;
        try
        {
            _textBox.Text = AllowMultiple ? ReplaceLastEntry(_textBox.Text, suggestion.Symbol) : suggestion.Symbol;
            _textBox.CaretIndex = _textBox.Text.Length;
        }
        finally
        {
            _accepting = false;
        }

        _popup.IsOpen = false;
    }

    private string CurrentTerm(string text)
    {
        if (!AllowMultiple)
            return text.Trim();

        var start = text.LastIndexOfAny(Separators) + 1;
        return text[start..].Trim();
    }

    private static string ReplaceLastEntry(string text, string symbol)
    {
        var start = text.LastIndexOfAny(Separators) + 1;
        var head = text[..start].TrimEnd(Separators).TrimEnd();
        return head.Length == 0 ? $"{symbol}, " : $"{head}, {symbol}, ";
    }
}
