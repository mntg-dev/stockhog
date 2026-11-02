using Stockhog.Application.Abstractions.Jobs;
using Stockhog.Application.Abstractions.Messaging;
using Stockhog.Application.Abstractions.Persistence;
using Stockhog.Application.Abstractions.Results;
using Stockhog.Application.Backtesting.Strategies;
using Stockhog.Application.Common;
using Stockhog.Application.Scanning.ExecuteScan;
using Stockhog.Domain.Backtesting;
using Stockhog.Domain.Common;
using Stockhog.Domain.MarketData;
using Stockhog.Domain.Scanning;
using FluentValidation;

namespace Stockhog.Application.Scanning.StartScan;

/// <summary>
/// Creates a scan of every strategy on every symbol and queues it as one background job: it brings the symbols'
/// minute data up to date, then backtests the pairs <paramref name="MaxParallel"/> engine processes at a time.
/// </summary>
/// <param name="ScreenerRunId">The screener run the symbols came from, if any; kept for reference.</param>
public sealed record StartScanCommand(
    IReadOnlyList<string> Symbols,
    IReadOnlyList<string> Strategies,
    DateOnly Start,
    DateOnly End,
    decimal InitialCapital,
    int MinTrades,
    decimal MaxDrawdownPercent,
    bool RequireBothHalvesProfitable,
    int MaxParallel,
    Guid? ScreenerRunId = null) : ICommand<ScanStarted>;

public sealed record ScanStarted(string ScanId, Guid JobId, int Candidates);

internal sealed class StartScanCommandValidator : AbstractValidator<StartScanCommand>
{
    /// <summary>Each half of the period must hold a few months for the consistency check to mean something.</summary>
    public const int MinPeriodDays = 120;

    public StartScanCommandValidator(TimeProvider clock, IStrategyCatalog strategies)
    {
        RuleFor(c => c.Symbols)
            .NotEmpty().WithMessage("Choose at least one symbol.")
            .Must(s => s.Distinct(StringComparer.OrdinalIgnoreCase).Count() <= ScanRun.MaxSymbols)
            .WithMessage($"At most {ScanRun.MaxSymbols} symbols per scan.");
        RuleForEach(c => c.Symbols).MustBeSymbol();

        RuleFor(c => c.Strategies)
            .NotEmpty().WithMessage("Choose at least one strategy.")
            .Must(s => s.Distinct().Count() <= ScanRun.MaxStrategies)
            .WithMessage($"At most {ScanRun.MaxStrategies} strategies per scan.");
        RuleForEach(c => c.Strategies)
            .MustBeStrategyName()
            .Must(name => strategies.Find(name) is not null).WithMessage("Unknown strategy '{PropertyValue}'.");

        RuleFor(c => c.End)
            .GreaterThanOrEqualTo(c => c.Start.AddDays(MinPeriodDays))
            .WithMessage($"The period must be at least {MinPeriodDays} days long.")
            .MustNotBeInTheFuture(clock);
        RuleFor(c => c.InitialCapital).GreaterThan(0).WithMessage("Initial capital must be positive.");
        RuleFor(c => c.MinTrades).InclusiveBetween(0, ScanThresholds.MaxMinTrades)
            .WithMessage($"The minimum number of trades must be between 0 and {ScanThresholds.MaxMinTrades}.");
        RuleFor(c => c.MaxDrawdownPercent).GreaterThan(0).LessThanOrEqualTo(100)
            .WithMessage("The drawdown limit must be above 0% and at most 100%.");
        RuleFor(c => c.MaxParallel).InclusiveBetween(1, ScanRun.MaxParallelLimit)
            .WithMessage($"Between 1 and {ScanRun.MaxParallelLimit} backtests can run in parallel.");
    }
}

internal sealed class StartScanCommandHandler(IScanRunRepository repository, IUnitOfWork unitOfWork, IJobQueue jobs, TimeProvider clock)
    : ICommandHandler<StartScanCommand, ScanStarted>
{
    public async Task<Result<ScanStarted>> HandleAsync(StartScanCommand command, CancellationToken cancellationToken)
    {
        var run = ScanRun.Create(
            ScanRunId.New(clock.GetLocalNow()),
            command.Symbols.Select(Symbol.Create).ToList(),
            command.Strategies.Select(StrategyName.Create).ToList(),
            DateRange.Create(command.Start, command.End),
            Money.Usd(command.InitialCapital),
            ScanThresholds.Create(command.MinTrades, command.MaxDrawdownPercent, command.RequireBothHalvesProfitable),
            command.MaxParallel,
            command.ScreenerRunId,
            clock.GetUtcNow());

        // Saved before queuing: the job loads the scan from storage and may start right away.
        repository.Add(run);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        var id = run.Id.Value;
        var job = jobs.Enqueue(
            $"Scan {run.Symbols.Count} symbols × {run.Strategies.Count} strategies",
            context => ExecuteAsync(id, context),
            ScanKeys.JobKeyPrefix + id);

        return new ScanStarted(id, job.Job.Id, run.Candidates.Count);
    }

    private static async Task<JobOutcome> ExecuteAsync(string scanId, JobExecutionContext context)
    {
        var command = new ExecuteScanCommand(scanId)
        {
            Progress = new DelegateProgress<ScanProgress>(p => context.Progress.Report(new JobProgress(p.Percent, p.Describe())))
        };

        var result = await context.Dispatcher.SendAsync(command, context.CancellationToken);
        return result.Match(
            outcome => outcome.Status == ScanStatus.Completed
                ? JobOutcome.Succeeded($"{outcome.Passed} of {outcome.Evaluated} evaluated candidates passed ({outcome.Total} in total).")
                : JobOutcome.Failed(outcome.FailureReason ?? outcome.Status.ToString()),
            error => JobOutcome.Failed(error.Message));
    }
}
