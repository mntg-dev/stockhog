using System.Reflection;
using Stockhog.Application.Backtesting;
using Stockhog.Application.Backtesting.Strategies;
using Stockhog.Application.MarketData;
using Stockhog.Domain.MarketData;
using Stockhog.Infrastructure;
using Stockhog.Application.Assistant;
using Stockhog.Infrastructure.Alpaca;
using Stockhog.Infrastructure.Anthropic;
using Stockhog.Infrastructure.Lean;
using Stockhog.Infrastructure.MarketData;
using Stockhog.Presentation.Shell;
using Stockhog.Wpf.Controls;
using Stockhog.Strategies;
using NetArchTest.Rules;

namespace Stockhog.Architecture.Tests;

/// <summary>
/// Clean Architecture dependency rule: dependencies point inwards
/// (App → Presentation / Infrastructure adapters → Application → Domain), and adapters stay independent of each other.
/// </summary>
public class LayerDependencyTests
{
    private const string ApplicationNamespace = "Stockhog.Application";
    private const string InfrastructureNamespace = "Stockhog.Infrastructure";
    private const string AlpacaNamespace = "Stockhog.Infrastructure.Alpaca";
    private const string LeanNamespace = "Stockhog.Infrastructure.Lean";
    private const string AnthropicNamespace = "Stockhog.Infrastructure.Anthropic";
    private const string StrategiesNamespace = "Stockhog.Strategies";
    private const string PresentationNamespace = "Stockhog.Presentation";
    private const string AppNamespace = "Stockhog.App";
    private static readonly string[] UiFrameworks = ["System.Windows", "LiveCharts", "CommunityToolkit.Mvvm"];

    private static readonly Assembly DomainAssembly = typeof(Timeframe).Assembly;
    private static readonly Assembly ApplicationAssembly = typeof(IBacktestEngine).Assembly;
    private static readonly Assembly InfrastructureAssembly = typeof(InfrastructureDependencyInjection).Assembly;
    private static readonly Assembly AlpacaAssembly = typeof(AlpacaDependencyInjection).Assembly;
    private static readonly Assembly LeanAssembly = typeof(LeanDependencyInjection).Assembly;
    private static readonly Assembly AnthropicAssembly = typeof(AnthropicDependencyInjection).Assembly;
    private static readonly Assembly StrategiesAssembly = typeof(StrategyBase).Assembly;
    private static readonly Assembly PresentationAssembly = typeof(ShellViewModel).Assembly;
    private static readonly Assembly ControlsAssembly = typeof(KpiCard).Assembly;

    [Fact]
    public void Domain_depends_on_no_other_layer_or_framework()
    {
        AssertNoDependency(DomainAssembly,
            ApplicationNamespace, InfrastructureNamespace, PresentationNamespace, AppNamespace, StrategiesNamespace,
            "QuantConnect", "Newtonsoft", "Microsoft.Extensions", "Microsoft.EntityFrameworkCore", "System.Windows");
    }

    [Fact]
    public void Application_does_not_depend_on_outer_layers_or_engine()
    {
        AssertNoDependency(ApplicationAssembly,
            InfrastructureNamespace, PresentationNamespace, AppNamespace, StrategiesNamespace,
            "QuantConnect", "Newtonsoft", "Microsoft.EntityFrameworkCore", "System.Windows");
    }

    [Fact]
    public void Presentation_does_not_depend_on_infrastructure()
    {
        AssertNoDependency(PresentationAssembly,
            InfrastructureNamespace, AppNamespace, StrategiesNamespace, "QuantConnect", "Microsoft.EntityFrameworkCore", "Serilog");
    }

    [Fact]
    public void Controls_library_knows_nothing_about_the_app()
    {
        // Reusable controls take their own simple types; view models map application data onto them.
        AssertNoDependency(ControlsAssembly,
            "Stockhog.Domain", ApplicationNamespace, InfrastructureNamespace, PresentationNamespace, AppNamespace, StrategiesNamespace);
    }

    [Fact]
    public void Shared_infrastructure_knows_no_adapter_or_engine()
    {
        AssertNoDependency(InfrastructureAssembly,
            [AlpacaNamespace, LeanNamespace, AnthropicNamespace, StrategiesNamespace, "QuantConnect", PresentationNamespace, AppNamespace, .. UiFrameworks]);
    }

    [Fact]
    public void Adapters_are_independent_of_each_other_and_of_the_ui()
    {
        AssertNoDependency(AlpacaAssembly,
            [LeanNamespace, AnthropicNamespace, StrategiesNamespace, "QuantConnect", "Microsoft.EntityFrameworkCore", PresentationNamespace, AppNamespace, .. UiFrameworks]);
        AssertNoDependency(LeanAssembly,
            [AlpacaNamespace, AnthropicNamespace, "Microsoft.EntityFrameworkCore", PresentationNamespace, AppNamespace, .. UiFrameworks]);
        AssertNoDependency(AnthropicAssembly,
            [AlpacaNamespace, LeanNamespace, StrategiesNamespace, "QuantConnect", "Microsoft.EntityFrameworkCore", PresentationNamespace, AppNamespace, .. UiFrameworks]);
    }

    [Fact]
    public void Strategies_depend_only_on_the_engine()
    {
        // They run inside the Lean engine process and must not pull in the app.
        AssertNoDependency(StrategiesAssembly,
            ApplicationNamespace, "Stockhog.Domain", InfrastructureNamespace, PresentationNamespace, AppNamespace,
            "Microsoft.EntityFrameworkCore", "Microsoft.Extensions");
    }

    [Fact]
    public void Port_implementations_are_not_public()
    {
        // Outer layers talk to Application ports and infrastructure seams; concrete adapters stay internal.
        Type[] ports =
        [
            typeof(IBacktestEngine), typeof(IBacktestResultReader), typeof(IMarketDataDownloader), typeof(IStrategyCatalog),
            typeof(IHistoricalBarSource), typeof(IMarketDataStore), typeof(IAssistantClient), typeof(IAssistantCredentials)
        ];

        foreach (var assembly in new[] { InfrastructureAssembly, AlpacaAssembly, LeanAssembly, AnthropicAssembly })
        {
            var publicImplementations = assembly.GetTypes()
                .Where(t => t is { IsClass: true, IsAbstract: false } && (t.IsPublic || t.IsNestedPublic))
                .Where(t => ports.Any(port => port.IsAssignableFrom(t)))
                .Select(t => t.FullName);

            Assert.Empty(publicImplementations);
        }
    }

    private static void AssertNoDependency(Assembly assembly, params string[] forbidden)
    {
        var result = Types.InAssembly(assembly)
            .ShouldNot().HaveDependencyOnAny(forbidden)
            .GetResult();

        Assert.True(result.IsSuccessful, Describe(result));
    }

    private static string Describe(NetArchTest.Rules.TestResult result) =>
        "Violating types: " + string.Join(", ", result.FailingTypeNames ?? []);
}
