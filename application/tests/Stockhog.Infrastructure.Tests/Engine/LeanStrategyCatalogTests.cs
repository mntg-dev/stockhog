using Stockhog.Application.Backtesting.Strategies;
using Stockhog.Domain.Backtesting;
using Stockhog.Infrastructure.Lean.Strategies;
using Stockhog.Strategies;

namespace Stockhog.Infrastructure.Tests.Engine;

/// <summary>
/// The real strategies assembly, discovered by reflection.
/// </summary>
public class LeanStrategyCatalogTests
{
    private readonly LeanStrategyCatalog _catalog = new();

    [Fact]
    public void Discovers_every_strategy_with_its_declared_parameters()
    {
        Assert.Equal(
        [
            "BollingerReversionStrategy", "DonchianBreakoutStrategy", "MomentumTrendStrategy", "OpeningRangeBreakoutStrategy",
            "RsiMeanReversionStrategy", "RsiPullbackStrategy", "SmaCrossStrategy"
        ], _catalog.GetAll().Select(s => s.Name).Order());

        var smaCross = _catalog.Find(nameof(SmaCrossStrategy))!;
        var slow = smaCross.Parameters.Single(p => p.Name == SmaCrossStrategy.SlowPeriod);
        Assert.Equal((StrategyParameterKind.Integer, 130m, 3m, 2000m, SmaCrossStrategy.FastPeriod),
            (slow.Kind, slow.DefaultValue, slow.Minimum, slow.Maximum, slow.MustExceed));

        var atr = smaCross.Parameters.Single(p => p.Name == SmaCrossStrategy.AtrStopMultiplier);
        Assert.Equal((StrategyParameterKind.Decimal, 1.5m), (atr.Kind, atr.DefaultValue));
    }

    [Fact]
    public void Every_strategy_is_usable_with_its_defaults()
    {
        Assert.All(_catalog.GetAll(), strategy =>
        {
            Assert.True(StrategyName.IsValid(strategy.Name), $"{strategy.Name} is not a valid strategy name");
            Assert.Empty(strategy.Validate(new Dictionary<string, string>()));
            Assert.All(strategy.Parameters, p => Assert.True(StrategyParameters.IsValidName(p.Name), $"{strategy.Name}.{p.Name}"));
            Assert.All(strategy.Parameters.Where(p => p.MustExceed is not null),
                p => Assert.Contains(strategy.Parameters, other => other.Name == p.MustExceed));
        });
    }

    [Fact]
    public void Suggested_ranges_are_valid_values_of_their_parameter()
    {
        var donchian = _catalog.Find(nameof(DonchianBreakoutStrategy))!.Parameters.Single(p => p.Name == DonchianBreakoutStrategy.EntryPeriod);
        Assert.Equal(new SuggestedRange(10, 55, 5), donchian.Suggested);

        Assert.All(_catalog.GetAll().SelectMany(s => s.Parameters.Select(p => (Strategy: s.Name, Parameter: p))), x =>
        {
            if (x.Parameter.Suggested is not { } range)
                return;

            var where = $"{x.Strategy}.{x.Parameter.Name}";
            Assert.True(range.Step > 0 && range.From <= range.To, $"{where}: empty range");
            Assert.True(x.Parameter.TryParse(x.Parameter.FormatValue(range.From), out _, out var fromError), $"{where}: {fromError}");
            Assert.True(x.Parameter.TryParse(x.Parameter.FormatValue(range.To), out _, out var toError), $"{where}: {toError}");
            if (x.Parameter.Kind == StrategyParameterKind.Integer)
                Assert.True(range.Step == decimal.Truncate(range.Step), $"{where}: integer parameter with a fractional step");
        });
    }

    /// <summary>The Strategies page shows these texts; a strategy without them would show resource keys.</summary>
    [Fact]
    public void Every_strategy_is_described_in_both_languages()
    {
        string[] sections = ["Summary", "HowItWorks", "WhenItWorks", "HowToUse"];

        foreach (var file in new[] { "Strings.resx", "Strings.pl.resx" })
        {
            var keys = ResourceKeys(file);
            var missing = _catalog.GetAll()
                .SelectMany(s => sections.Select(section => $"StrategyDoc_{s.Name}_{section}"))
                .Where(key => !keys.Contains(key))
                .ToList();

            Assert.True(missing.Count == 0, $"{file} is missing: {string.Join(", ", missing)}");
        }
    }

    /// <summary>
    /// Names and descriptions of every strategy and parameter are translated, and the English resources repeat the
    /// attributes word for word, so the attributes stay the one place to edit English texts.
    /// </summary>
    [Fact]
    public void Strategy_and_parameter_names_are_translated_and_english_matches_the_attributes()
    {
        var expected = _catalog.GetAll()
            .SelectMany(s => new[] { ($"Strategy_{s.Name}_Name", s.DisplayName), ($"Strategy_{s.Name}_Description", s.Description) }
                .Concat(s.Parameters.SelectMany(p => new[]
                {
                    ($"StrategyParam_{s.Name}_{p.Name}_Name", p.DisplayName),
                    ($"StrategyParam_{s.Name}_{p.Name}_Description", p.Description)
                })))
            .ToList();

        var english = ResourceTexts("Strings.resx");
        var polish = ResourceKeys("Strings.pl.resx");

        var wrongEnglish = expected.Where(e => e.Item2 is null || !english.TryGetValue(e.Item1, out var text) || text != e.Item2)
            .Select(e => e.Item1).ToList();
        var missingPolish = expected.Select(e => e.Item1).Where(key => !polish.Contains(key)).ToList();

        Assert.True(wrongEnglish.Count == 0, $"Strings.resx is missing or differs from the attributes: {string.Join(", ", wrongEnglish)}");
        Assert.True(missingPolish.Count == 0, $"Strings.pl.resx is missing: {string.Join(", ", missingPolish)}");
    }

    private static HashSet<string> ResourceKeys(string file) => ResourceTexts(file).Keys.ToHashSet(StringComparer.Ordinal);

    private static Dictionary<string, string> ResourceTexts(string file)
    {
        // The Presentation project targets Windows, so its resources are read from source rather than referenced.
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "StockhogSolution.slnx")))
            directory = directory.Parent;
        Assert.NotNull(directory);

        var path = Path.Combine(directory.FullName, "Stockhog.Presentation", "Resources", file);
        return System.Xml.Linq.XDocument.Load(path).Root!.Elements("data")
            .ToDictionary(e => (string)e.Attribute("name")!, e => (string)e.Element("value")!, StringComparer.Ordinal);
    }

    [Fact]
    public void Unknown_strategy_is_not_found()
    {
        Assert.Null(_catalog.Find("StrategyBase"));
        Assert.Null(_catalog.Find("smacrossstrategy"));
    }
}
