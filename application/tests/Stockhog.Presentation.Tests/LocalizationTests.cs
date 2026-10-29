using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Resources;
using System.Text.RegularExpressions;
using System.Windows.Data;
using Stockhog.Application.Abstractions.Jobs;
using Stockhog.Presentation.Localization;

namespace Stockhog.Presentation.Tests;

/// <summary>
/// <see cref="Loc"/> holds the process-wide UI culture, so these tests run in one non-parallel collection.
/// </summary>
[Collection(nameof(CultureCollection))]
public sealed class LocalizationTests : IDisposable
{
    private readonly CultureInfo _original = Loc.Instance.Culture;

    [Fact]
    public void Polish_and_English_define_exactly_the_same_keys()
    {
        var english = Keys(CultureInfo.InvariantCulture);
        // Strings.pl.resx compiles to the neutral "pl" satellite; pl-PL falls back to it at runtime.
        var polish = Keys(Loc.Polish.Parent);

        Assert.NotEmpty(english);
        Assert.Empty(english.Except(polish));
        Assert.Empty(polish.Except(english));
    }

    [Fact]
    public void Every_key_used_in_xaml_or_view_models_exists()
    {
        var root = FindSolutionRoot();
        var usage = new Regex(@"loc:Tr (?<key>\w+)|Loc\.(?:Get|Format)\(""(?<key>\w+)""|NavigationItem\(""(?<key>\w+)""");
        var sources = Directory.EnumerateFiles(Path.Combine(root, "Stockhog.App"), "*.xaml", SearchOption.AllDirectories)
            .Concat(Directory.EnumerateFiles(Path.Combine(root, "Stockhog.Presentation"), "*.cs", SearchOption.AllDirectories))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"));

        var used = sources.SelectMany(f => usage.Matches(File.ReadAllText(f)).Select(m => m.Groups["key"].Value)).ToHashSet();
        var defined = Keys(CultureInfo.InvariantCulture);

        Assert.True(used.Count > 50, $"only {used.Count} keys found; is the search pattern still right?");
        Assert.Empty(used.Except(defined));
    }

    [Fact]
    public void Changing_the_culture_switches_texts_and_notifies_bindings()
    {
        Loc.Instance.SetCulture(Loc.English);
        var changed = new List<string?>();
        PropertyChangedEventHandler handler = (_, e) => changed.Add(e.PropertyName);
        Loc.Instance.PropertyChanged += handler;

        try
        {
            Assert.Equal("Cancel", Loc.Get("Common_Cancel"));
            Loc.Instance.SetCulture(Loc.Polish);
            Assert.Equal("Anuluj", Loc.Get("Common_Cancel"));
            Assert.Equal("Zakres: 1 – 5.", Loc.Format("Strategy_Range", "1 – 5"));
        }
        finally
        {
            Loc.Instance.PropertyChanged -= handler;
        }

        Assert.Contains(Binding.IndexerName, changed);
        Assert.Contains(nameof(Loc.Culture), changed);
        Assert.Equal(Loc.Polish, CultureInfo.CurrentUICulture);
    }

    [Fact]
    public void Missing_key_is_visible_instead_of_empty()
    {
        Assert.Equal("[No_Such_Key]", Loc.Get("No_Such_Key"));
    }

    [Fact]
    public void Enum_values_are_shown_in_the_current_language()
    {
        var converter = new LocalizedEnumConverter();

        Loc.Instance.SetCulture(Loc.English);
        Assert.Equal("Running", converter.Convert(JobStatus.Running, typeof(string), null, CultureInfo.InvariantCulture));

        Loc.Instance.SetCulture(Loc.Polish);
        Assert.Equal("W toku", converter.Convert(JobStatus.Running, typeof(string), null, CultureInfo.InvariantCulture));
        Assert.Equal("Monday", converter.Convert(DayOfWeek.Monday, typeof(string), null, CultureInfo.InvariantCulture));
    }

    public void Dispose() => Loc.Instance.SetCulture(_original);

    private static HashSet<string> Keys(CultureInfo culture)
    {
        var manager = new ResourceManager("Stockhog.Presentation.Resources.Strings", typeof(Loc).Assembly);
        var set = manager.GetResourceSet(culture, createIfNotExists: true, tryParents: false)!;
        return set.Cast<System.Collections.DictionaryEntry>().Select(e => (string)e.Key).ToHashSet();
    }

    private static string FindSolutionRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (dir.EnumerateFiles("*.slnx").Any())
                return dir.FullName;
        }

        throw new InvalidOperationException("Solution folder not found.");
    }
}

[CollectionDefinition(nameof(CultureCollection), DisableParallelization = true)]
public sealed class CultureCollection;
