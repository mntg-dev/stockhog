using CommunityToolkit.Mvvm.Messaging;
using Stockhog.Application.Abstractions.Messaging;
using Stockhog.Application.Instruments;
using Stockhog.Presentation.Shell;
using Stockhog.Wpf.Controls;
using Microsoft.Extensions.Logging;

namespace Stockhog.Presentation.Instruments;

/// <summary>
/// Symbol suggestions for every <see cref="SymbolBox"/>. Keeps the stored instrument list (a few thousand entries) in
/// memory and searches it on each keystroke; reloads it when a refresh finishes.
/// </summary>
public sealed class InstrumentCatalog : ISymbolSuggestionProvider, IRecipient<InstrumentsChangedMessage>
{
    private readonly IDispatcher _dispatcher;
    private readonly ILogger<InstrumentCatalog> _logger;
    private volatile IReadOnlyList<InstrumentInfo> _instruments = [];
    private volatile IReadOnlyDictionary<string, InstrumentInfo> _bySymbol = new Dictionary<string, InstrumentInfo>();

    public InstrumentCatalog(IDispatcher dispatcher, IMessenger messenger, ILogger<InstrumentCatalog> logger)
    {
        _dispatcher = dispatcher;
        _logger = logger;
        messenger.RegisterAll(this);
    }

    public int Count => _instruments.Count;

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        var result = await _dispatcher.SendAsync(new GetInstrumentsQuery(), cancellationToken);
        if (result.IsSuccess)
        {
            _instruments = result.Value;
            _bySymbol = result.Value.ToDictionary(i => i.Symbol, StringComparer.OrdinalIgnoreCase);
        }
        else
            _logger.LogWarning("Could not load the instrument list: {Error}", result.Error.Message);
    }

    public async void Receive(InstrumentsChangedMessage message) => await LoadAsync();

    /// <summary>The instrument with this symbol, e.g. to show its company name; <c>null</c> if not in the list.</summary>
    public InstrumentInfo? Find(string symbol) => _bySymbol.GetValueOrDefault(symbol);

    /// <summary>Matches for the text, or with empty text every instrument in symbol order (the list is stored that way).</summary>
    public IReadOnlyList<SymbolSuggestion> Suggest(string text, int maxResults) =>
        (string.IsNullOrWhiteSpace(text) ? _instruments.Take(maxResults) : Search(_instruments, text, maxResults))
            .Select(i => new SymbolSuggestion(i.Symbol, i.Name, i.Exchange))
            .ToList();

    /// <summary>
    /// Best matches first: the exact symbol; symbols starting with the text (shortest first, so "F" finds Ford before
    /// FDX); names starting with it; names with a later word starting with it; and from three letters on, names
    /// containing it anywhere. Name matches are ordered by name length: "Microsoft Corporation Common Stock" is a
    /// closer match for "micro" than "MicroSectors U.S. Big Banks 3x Leveraged ETNs".
    /// </summary>
    public static IEnumerable<InstrumentInfo> Search(IReadOnlyList<InstrumentInfo> instruments, string text, int maxResults)
    {
        var term = text.Trim();
        if (term.Length == 0 || maxResults <= 0)
            return [];

        return instruments
            .Select(i => (Instrument: i, Rank: Rank(i, term)))
            .Where(m => m.Rank != MatchRank.None)
            .OrderBy(m => m.Rank)
            .ThenBy(m => m.Rank <= MatchRank.SymbolPrefix ? m.Instrument.Symbol.Length : m.Instrument.Name.Length)
            .ThenBy(m => m.Instrument.Symbol, StringComparer.Ordinal)
            .Take(maxResults)
            .Select(m => m.Instrument);
    }

    private enum MatchRank
    {
        ExactSymbol,
        SymbolPrefix,
        NameStart,
        NameWordStart,
        InsideName,
        None
    }

    private static MatchRank Rank(InstrumentInfo instrument, string term)
    {
        if (instrument.Symbol.Equals(term, StringComparison.OrdinalIgnoreCase))
            return MatchRank.ExactSymbol;
        if (instrument.Symbol.StartsWith(term, StringComparison.OrdinalIgnoreCase))
            return MatchRank.SymbolPrefix;
        if (instrument.Name.StartsWith(term, StringComparison.OrdinalIgnoreCase))
            return MatchRank.NameStart;

        for (var index = instrument.Name.IndexOf(term, StringComparison.OrdinalIgnoreCase);
             index > 0;
             index = instrument.Name.IndexOf(term, index + 1, StringComparison.OrdinalIgnoreCase))
        {
            if (!char.IsLetterOrDigit(instrument.Name[index - 1]))
                return MatchRank.NameWordStart;
        }

        // Inside a word only from a few letters on: "f" would otherwise match every name containing an f.
        return term.Length >= MinInWordMatch && instrument.Name.Contains(term, StringComparison.OrdinalIgnoreCase)
            ? MatchRank.InsideName
            : MatchRank.None;
    }

    private const int MinInWordMatch = 3;
}
