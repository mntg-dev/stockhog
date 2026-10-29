using Stockhog.Application.Abstractions.Jobs;
using Stockhog.Application.Abstractions.Messaging;
using Stockhog.Application.Abstractions.Results;
using Stockhog.Application.Common;
using Stockhog.Application.MarketData.DownloadMarketData;
using Stockhog.Domain.MarketData;
using FluentValidation;

namespace Stockhog.Application.MarketData.QueueMarketDataDownloads;

/// <summary>
/// Queues one background download job per symbol. Returns immediately; progress is visible through <see cref="IJobMonitor"/>.
/// </summary>
/// <param name="IncludeBenchmark">Also keep daily data of the backtest benchmark (<see cref="BenchmarkSymbol"/>) up to date.</param>
public sealed record QueueMarketDataDownloadsCommand(
    IReadOnlyList<string> Symbols,
    Timeframe Timeframe,
    DateOnly Start,
    DateOnly End,
    bool IncludeBenchmark = true) : ICommand<QueuedDownloads>
{
    /// <summary>Lean's default benchmark; backtests compare against its daily bars.</summary>
    public const string BenchmarkSymbol = "SPY";

    public const int MaxSymbols = 100;
}

/// <param name="AlreadyQueued">Symbols skipped because a download for them is still queued or running.</param>
public sealed record QueuedDownloads(IReadOnlyList<Guid> JobIds, IReadOnlyList<string> AlreadyQueued);

internal sealed class QueueMarketDataDownloadsCommandValidator : AbstractValidator<QueueMarketDataDownloadsCommand>
{
    public QueueMarketDataDownloadsCommandValidator(TimeProvider clock)
    {
        RuleFor(c => c.Symbols)
            .NotEmpty().WithMessage("Enter at least one symbol.")
            .Must(s => s.Count <= QueueMarketDataDownloadsCommand.MaxSymbols)
            .WithMessage($"At most {QueueMarketDataDownloadsCommand.MaxSymbols} symbols at once.");
        RuleForEach(c => c.Symbols).MustBeSymbol();
        RuleFor(c => c.Timeframe).IsInEnum();
        RuleFor(c => c.Start).MustNotBeInTheFuture(clock);
        RuleFor(c => c.End).GreaterThanOrEqualTo(c => c.Start).WithMessage("End date must not be before start date.");
    }
}

internal sealed class QueueMarketDataDownloadsCommandHandler(IJobQueue jobs)
    : ICommandHandler<QueueMarketDataDownloadsCommand, QueuedDownloads>
{
    public Task<Result<QueuedDownloads>> HandleAsync(QueueMarketDataDownloadsCommand command, CancellationToken cancellationToken)
    {
        var downloads = command.Symbols
            .Select(s => new DownloadMarketDataCommand(Symbol.Create(s).Value, command.Timeframe, command.Start, command.End))
            .ToList();

        if (command.IncludeBenchmark)
        {
            downloads.Add(new DownloadMarketDataCommand(
                QueueMarketDataDownloadsCommand.BenchmarkSymbol, Timeframe.Daily, command.Start, command.End));
        }

        var queued = new List<Guid>();
        var alreadyQueued = new List<string>();

        foreach (var download in downloads.DistinctBy(d => (d.Symbol, d.Timeframe)))
        {
            var key = MarketDataSetId.For(Symbol.Create(download.Symbol), download.Timeframe).Value;
            var title = $"Download {download.Symbol} {download.Timeframe} {download.Start:yyyy-MM-dd}..{download.End:yyyy-MM-dd}";

            var result = jobs.Enqueue(title, context => RunAsync(download, context), key);
            if (result.WasQueued)
                queued.Add(result.Job.Id);
            else
                alreadyQueued.Add(download.Symbol);
        }

        return Task.FromResult(Result<QueuedDownloads>.Success(new QueuedDownloads(queued, alreadyQueued)));
    }

    private static async Task<JobOutcome> RunAsync(DownloadMarketDataCommand download, JobExecutionContext context)
    {
        var progress = new DelegateProgress<int>(percent => context.Progress.Report(new JobProgress(percent)));
        var result = await context.Dispatcher.SendAsync(download with { Progress = progress }, context.CancellationToken);

        return result.Match(r => JobOutcome.Succeeded(r.Describe()), error => JobOutcome.Failed(error.Message));
    }
}
