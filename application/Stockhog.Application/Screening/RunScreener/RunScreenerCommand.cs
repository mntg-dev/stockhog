using System.Collections.Concurrent;
using Stockhog.Application.Abstractions.Jobs;
using Stockhog.Application.Abstractions.Messaging;
using Stockhog.Application.Abstractions.Results;
using Stockhog.Application.Common;
using Stockhog.Application.Instruments;
using FluentValidation;
using Microsoft.Extensions.Caching.Hybrid;

namespace Stockhog.Application.Screening.RunScreener;

/// <summary>
/// Measures every listed instrument on the chosen exchanges from a year of daily bars, keeps those that pass the
/// criteria, ranks them and saves the result. The universe is fetched in batches, a few at a time; the shared rate
/// limiter keeps the requests within the plan's limit.
/// </summary>
public sealed record RunScreenerCommand(ScreenerCriteria Criteria) : ICommand<ScreenerRunSummary>
{
    /// <summary>Batches requested at the same time.</summary>
    public const int MaxParallelBatches = 4;

    /// <summary>A year of sessions plus room for holidays.</summary>
    public const int LookbackDays = 380;

    /// <summary>Optional live progress for the caller; not part of the command's data.</summary>
    public IProgress<JobProgress>? Progress { get; init; }
}

internal sealed class ScreenerCriteriaValidator : AbstractValidator<ScreenerCriteria>
{
    public ScreenerCriteriaValidator()
    {
        RuleFor(c => c.MinPrice).GreaterThan(0).When(c => c.MinPrice is not null).WithMessage("The minimum price must be above zero.");
        RuleFor(c => c.MaxPrice).GreaterThan(0).When(c => c.MaxPrice is not null).WithMessage("The maximum price must be above zero.");
        RuleFor(c => c).Must(c => c.MinPrice is null || c.MaxPrice is null || c.MinPrice <= c.MaxPrice)
            .WithMessage("The minimum price must not be above the maximum price.");

        RuleFor(c => c.MinAverageDollarVolume).GreaterThanOrEqualTo(0).WithMessage("The minimum dollar volume must not be negative.");

        RuleFor(c => c.MinAtrPercent).GreaterThanOrEqualTo(0).When(c => c.MinAtrPercent is not null).WithMessage("The minimum ATR % must not be negative.");
        RuleFor(c => c.MaxAtrPercent).GreaterThan(0).When(c => c.MaxAtrPercent is not null).WithMessage("The maximum ATR % must be above zero.");
        RuleFor(c => c).Must(c => c.MinAtrPercent is null || c.MaxAtrPercent is null || c.MinAtrPercent <= c.MaxAtrPercent)
            .WithMessage("The minimum ATR % must not be above the maximum ATR %.");

        RuleFor(c => c.MinTradingDays)
            .InclusiveBetween(ScreenerCriteria.MinTradingDaysLimit, ScreenerCriteria.MaxTradingDaysLimit)
            .WithMessage($"The minimum history must be between {ScreenerCriteria.MinTradingDaysLimit} and {ScreenerCriteria.MaxTradingDaysLimit} sessions.");

        RuleFor(c => c.Exchanges)
            .NotEmpty().WithMessage("Choose at least one exchange.")
            .Must(e => e.All(x => ScreenerCriteria.AllExchanges.Contains(x, StringComparer.OrdinalIgnoreCase)))
            .WithMessage($"Exchanges must be among {string.Join(", ", ScreenerCriteria.AllExchanges)}.");

        RuleFor(c => c.SortBy).IsInEnum();
        RuleFor(c => c.MaxResults).InclusiveBetween(1, ScreenerCriteria.MaxResultsLimit)
            .WithMessage($"Keep between 1 and {ScreenerCriteria.MaxResultsLimit} results.");
    }
}

internal sealed class RunScreenerCommandValidator : AbstractValidator<RunScreenerCommand>
{
    public RunScreenerCommandValidator() => RuleFor(c => c.Criteria).NotNull().SetValidator(new ScreenerCriteriaValidator());
}

internal static class ScreenerErrors
{
    public static readonly Error SourceNotConfigured = Error.Failure(
        "Screener.SourceNotConfigured",
        "The screener needs your Alpaca keys: add them on the Settings page.");

    public static readonly Error NoInstruments = Error.Failure(
        "Screener.NoInstruments",
        "The instrument list is empty. Refresh it on the Market data page first.");

    public static readonly Error NoData = Error.Failure(
        "Screener.NoData",
        "No daily bars came back for any instrument; nothing was saved.");

    public static Error NotFound(Guid? id) => Error.NotFound(
        "Screener.NotFound",
        id is null ? "No screener run has finished yet." : $"Screener run {id} was not found.");
}

internal sealed class RunScreenerCommandHandler(
    IDailyBarSource bars,
    IInstrumentStore instruments,
    IScreenerResultStore results,
    HybridCache cache,
    TimeProvider clock) : ICommandHandler<RunScreenerCommand, ScreenerRunSummary>
{
    public async Task<Result<ScreenerRunSummary>> HandleAsync(RunScreenerCommand command, CancellationToken cancellationToken)
    {
        if (!bars.IsConfigured)
            return ScreenerErrors.SourceNotConfigured;

        var criteria = command.Criteria;
        var exchanges = new HashSet<string>(criteria.Exchanges, StringComparer.OrdinalIgnoreCase);
        var universe = (await instruments.ListAsync(cancellationToken).ConfigureAwait(false))
            .Where(i => exchanges.Contains(i.Exchange))
            .Where(i => !criteria.StocksOnly || !FundNames.LooksLikeFund(i.Name))
            .ToDictionary(i => i.Symbol, StringComparer.Ordinal);
        if (universe.Count == 0)
            return ScreenerErrors.NoInstruments;

        var end = ExchangeCalendar.LastCompletedSession(clock);
        var start = end.AddDays(-RunScreenerCommand.LookbackDays);
        var batches = universe.Keys.Chunk(Math.Max(1, bars.MaxSymbolsPerRequest)).ToList();

        var measured = new ConcurrentBag<SymbolMetrics>();
        var finishedBatches = 0;
        command.Progress?.Report(new JobProgress(0, $"Screening {universe.Count} instruments in {batches.Count} batches"));

        var parallel = new ParallelOptions { MaxDegreeOfParallelism = RunScreenerCommand.MaxParallelBatches, CancellationToken = cancellationToken };
        await Parallel.ForEachAsync(batches, parallel, async (batch, token) =>
        {
            var barsBySymbol = await bars.GetDailyBarsAsync(batch, start, end, token).ConfigureAwait(false);
            foreach (var (symbol, symbolBars) in barsBySymbol)
            {
                if (ScreenerCalculator.Measure(symbol, symbolBars) is { } metrics)
                    measured.Add(metrics);
            }

            var done = Interlocked.Increment(ref finishedBatches);
            command.Progress?.Report(new JobProgress(done * 95 / batches.Count, $"Measured {measured.Count} of {universe.Count} instruments"));
        }).ConfigureAwait(false);

        if (measured.IsEmpty)
            return ScreenerErrors.NoData;

        var newest = measured.Max(m => m.LastDate);
        var matched = ScreenerCalculator.Rank(measured.Where(m => ScreenerCalculator.Matches(m, criteria, newest)), criteria.SortBy)
            .Take(criteria.MaxResults)
            .Select((m, index) => new ScreenedSymbol(index + 1, universe[m.Symbol].Name, universe[m.Symbol].Exchange, m))
            .ToList();

        var summary = new ScreenerRunSummary(Guid.CreateVersion7(clock.GetUtcNow()), clock.GetUtcNow(), newest, criteria, universe.Count, measured.Count, matched.Count);
        await results.SaveAsync(new ScreenerResult(summary, matched), cancellationToken).ConfigureAwait(false);
        await cache.RemoveByTagAsync(ScreenerKeys.CacheTag, cancellationToken).ConfigureAwait(false);

        command.Progress?.Report(new JobProgress(100, $"{matched.Count} of {measured.Count} instruments match"));
        return summary;
    }
}
