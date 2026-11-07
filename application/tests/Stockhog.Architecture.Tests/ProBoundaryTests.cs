using System.IO;
using System.Text.RegularExpressions;
using Stockhog.Presentation;
using Stockhog.Presentation.Shell;
using Microsoft.Extensions.DependencyInjection;

namespace Stockhog.Architecture.Tests;

/// <summary>
/// The open-core boundary, while the Pro edition is moved out of this repository step by step: the free core may use
/// Pro code only where it does today, and that list may only shrink. Pro areas are folders named as in
/// <see cref="ProAreas"/> in the Domain, Application and Presentation projects.
/// </summary>
public partial class ProBoundaryTests
{
    private static readonly string[] ProAreas = ["Screening", "Scanning", "Signals", "Proposals", "Autopilot", "Optimization", "Analytics", "Assistant"];

    /// <summary>
    /// Free files that still reach into Pro code. Remove a line when its file no longer does; never add one: give the
    /// Pro code a seam instead (a registration, a port), as the menu got with <see cref="PageRegistration"/>.
    /// </summary>
    private static readonly HashSet<string> KnownCrossings =
    [
        // Composition roots: they will register Pro modules through one entry point.
        "Stockhog.Application/ApplicationDependencyInjection.cs",
        "Stockhog.Infrastructure/InfrastructureDependencyInjection.cs",
        "Stockhog.Infrastructure.Alpaca/AlpacaDependencyInjection.cs",
        "Stockhog.Infrastructure.Anthropic/AnthropicDependencyInjection.cs",
        "Stockhog.Infrastructure.Lean/LeanDependencyInjection.cs",
        "Stockhog.Presentation/PresentationDependencyInjection.cs",
        // Pro adapters that still live in shared projects.
        "Stockhog.App/Views/AssistantView.xaml.cs",
        "Stockhog.Infrastructure.Alpaca/MarketData/AlpacaDailyBarsClient.cs",
        "Stockhog.Infrastructure.Anthropic/ClaudeAssistantClient.cs",
        "Stockhog.Infrastructure.Anthropic/ClaudeConversation.cs",
        "Stockhog.Infrastructure.Anthropic/ProtectedApiKeyStore.cs",
        "Stockhog.Infrastructure.Lean/Engine/Results/StrategyStateReader.cs",
        "Stockhog.Infrastructure/Persistence/AutopilotRepositories.cs",
        "Stockhog.Infrastructure/Persistence/Configurations/AutopilotConfiguration.cs",
        "Stockhog.Infrastructure/Persistence/Configurations/OptimizationRunConfiguration.cs",
        "Stockhog.Infrastructure/Persistence/Configurations/ScanRunConfiguration.cs",
        "Stockhog.Infrastructure/Persistence/Configurations/TradeProposalConfiguration.cs",
        "Stockhog.Infrastructure/Persistence/OptimizationRepositories.cs",
        "Stockhog.Infrastructure/Persistence/ProposalRepositories.cs",
        "Stockhog.Infrastructure/Persistence/ScanRepositories.cs",
        "Stockhog.Infrastructure/Persistence/ScreenerResultStore.cs",
        // One database for both editions: Pro tables and startup chores.
        "Stockhog.Infrastructure/Persistence/TradingDbContext.cs",
        "Stockhog.Infrastructure/Persistence/DatabaseInitializer.cs",
        // Backtests know the optimization they belong to, and use the shared metrics.
        "Stockhog.Domain/Backtesting/BacktestRun.cs",
        "Stockhog.Domain/Backtesting/BacktestRunId.cs",
        "Stockhog.Domain/Backtesting/PerformanceMetrics.cs",
        "Stockhog.Infrastructure/Persistence/Configurations/BacktestRunConfiguration.cs",
        "Stockhog.Application/Backtesting/BackfillMetrics/BackfillBacktestMetricsCommand.cs",
        "Stockhog.Application/Backtesting/RunBacktest/RunBacktestCommandHandler.cs",
        "Stockhog.Application/Backtesting/RunBacktest/RunBacktestCommandValidator.cs",
        // Free pages that show or link Pro things.
        "Stockhog.Presentation/Guide/GuideViewModel.cs",
        "Stockhog.Presentation/Home/HomeViewModel.cs",
        "Stockhog.Presentation/Jobs/JobsViewModel.cs",
        "Stockhog.Presentation/Settings/SettingsViewModel.cs",
        "Stockhog.Presentation/Strategies/StrategiesViewModel.cs",
        "Stockhog.Presentation/Trading/OrderTicket.cs"
    ];

    private static readonly string[] FreeProjects =
    [
        "Stockhog.Domain", "Stockhog.Application", "Stockhog.Infrastructure", "Stockhog.Infrastructure.Alpaca", "Stockhog.Infrastructure.Lean",
        "Stockhog.Infrastructure.Anthropic", "Stockhog.Presentation", "Stockhog.App", "Stockhog.Strategies"
    ];

    [Fact]
    public void The_free_core_reaches_into_pro_code_only_where_it_already_did()
    {
        var root = SolutionRoot();
        var crossings = FreeProjects
            .SelectMany(project => Directory.EnumerateFiles(Path.Combine(root, project), "*.cs", SearchOption.AllDirectories))
            .Select(file => (File: file, Relative: Path.GetRelativePath(root, file).Replace('\\', '/')))
            .Where(f => !f.Relative.Contains("/obj/") && !f.Relative.Contains("/bin/") && !f.Relative.Contains("/Migrations/"))
            .Where(f => !ProAreas.Any(area => f.Relative.Contains($"/{area}/")))
            .Where(f => ProNamespace().IsMatch(File.ReadAllText(f.File)))
            .Select(f => f.Relative)
            .ToHashSet();

        Assert.Empty(crossings.Except(KnownCrossings));
        // A file that no longer crosses must leave the list, so it cannot quietly start again.
        Assert.Empty(KnownCrossings.Except(crossings));
    }

    [Fact]
    public void Every_page_of_a_pro_area_is_tagged_with_its_feature_and_no_free_page_is()
    {
        var entries = new ServiceCollection().AddPresentationServices()
            .Where(d => d.ServiceType == typeof(PageEntry))
            .Select(d => (PageEntry)d.ImplementationInstance!)
            .ToList();

        Assert.NotEmpty(entries);
        Assert.All(entries, e =>
        {
            var isPro = ProAreas.Any(area => e.PageType.Namespace!.EndsWith("." + area, StringComparison.Ordinal));
            Assert.True(isPro == e.Feature.HasValue, $"{e.PageType.Name}: Pro area {isPro}, feature {e.Feature}");
        });
    }

    [GeneratedRegex(@"Stockhog\.(?:Domain|Application|Presentation|Infrastructure)\.(?:Screening|Scanning|Signals|Proposals|Autopilot|Optimization|Analytics|Assistant)\b")]
    private static partial Regex ProNamespace();

    private static string SolutionRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "StockhogSolution.slnx")))
                return dir.FullName;
        }

        throw new InvalidOperationException("Solution folder not found.");
    }
}
