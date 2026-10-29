using Stockhog.Application.Instruments;
using Stockhog.Presentation.Instruments;

namespace Stockhog.Presentation.Tests;

public class InstrumentCatalogTests
{
    private static readonly IReadOnlyList<InstrumentInfo> Instruments =
    [
        new("AAPL", "Apple Inc. Common Stock", "NASDAQ"),
        new("APLE", "Apple Hospitality REIT, Inc.", "NYSE"),
        new("F", "Ford Motor Company", "NYSE"),
        new("FDX", "FedEx Corporation", "NYSE"),
        new("MSFT", "Microsoft Corporation", "NASDAQ"),
        new("PINE", "Alpine Income Property Trust", "NYSE")
    ];

    [Fact]
    public void Exact_symbol_comes_first_then_shorter_symbols()
    {
        // "Microsoft" contains an f too, but single letters only match symbols and word starts.
        Assert.Equal(["F", "FDX"], Symbols("f"));
    }

    [Fact]
    public void Symbol_prefix_ranks_above_name_matches()
    {
        // APLE starts with "ap"; AAPL only matches through "Apple" in its name.
        Assert.Equal(["APLE", "AAPL"], Symbols("ap"));
    }

    [Fact]
    public void Word_start_in_the_name_ranks_above_a_match_inside_a_word()
    {
        // MSFT: "Microsoft" contains "soft" inside a word; nothing else matches.
        Assert.Equal(["MSFT"], Symbols("soft"));
        // PINE matches by symbol; "Alpine" also contains "pine", but the symbol match is better.
        Assert.Equal(["PINE"], Symbols("pine"));
        // "Motor" starts a word in Ford's name.
        Assert.Equal(["F"], Symbols("motor"));
    }

    [Fact]
    public void Shorter_names_rank_first_among_name_matches()
    {
        IReadOnlyList<InstrumentInfo> instruments =
        [
            new("BNKU", "MicroSectors U.S. Big Banks 3x Leveraged ETNs", "ARCA"),
            new("MSFT", "Microsoft Corporation Common Stock", "NASDAQ"),
            new("AMD", "Advanced Micro Devices, Inc. Common Stock", "NASDAQ")
        ];

        // Names starting with "micro" first, the shorter one first; a later word starting with it after that.
        Assert.Equal(["MSFT", "BNKU", "AMD"], InstrumentCatalog.Search(instruments, "micro", 10).Select(i => i.Symbol));
    }

    [Fact]
    public void Two_letters_do_not_match_inside_words()
    {
        Assert.DoesNotContain("MSFT", Symbols("of"));
    }

    [Fact]
    public void Blank_text_and_no_match_return_nothing()
    {
        Assert.Empty(Symbols("  "));
        Assert.Empty(Symbols("zzz"));
    }

    [Fact]
    public void Results_are_limited()
    {
        Assert.Single(InstrumentCatalog.Search(Instruments, "a", maxResults: 1));
    }

    private static List<string> Symbols(string text) =>
        InstrumentCatalog.Search(Instruments, text, maxResults: 10).Select(i => i.Symbol).ToList();
}
