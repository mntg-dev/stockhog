using Stockhog.Application.Abstractions.Jobs;
using Stockhog.Application.Abstractions.Messaging;
using Stockhog.Application.Abstractions.Results;
using Stockhog.Application.Backtesting.RunBacktest;
using Stockhog.Application.Common;
using Stockhog.Domain.MarketData;
using FluentValidation;

namespace Stockhog.Application.Backtesting.QueueBacktests;

/// <summary>
/// Runs the same strategy over several symbols as background jobs. Each run is a separate engine process,
/// so up to <c>Jobs:MaxParallelJobs</c> backtests execute at the same time.
/// </summary>
public sealed record QueueBacktestsCommand(
    string StrategyName,
    IReadOnlyList<string> Symbols,
    DateOnly Start,
    DateOnly End,
    decimal InitialCapital,
    IReadOnlyDictionary<string, string> Parameters) : ICommand<QueuedBacktests>
{
    public const int MaxSymbols = 100;

    internal RunBacktestCommand ForSymbol(string symbol) => new(StrategyName, symbol, Start, End, InitialCapital, Parameters);
}

/// <param name="AlreadyQueued">Symbols skipped because a backtest of the same strategy for them is still queued or running.</param>
public sealed record QueuedBacktests(IReadOnlyList<Guid> JobIds, IReadOnlyList<string> AlreadyQueued);

internal sealed class QueueBacktestsCommandValidator : AbstractValidator<QueueBacktestsCommand>
{
    public QueueBacktestsCommandValidator(IValidator<RunBacktestCommand> runValidator)
    {
        RuleFor(c => c.Symbols)
            .NotEmpty().WithMessage("Enter at least one symbol.")
            .Must(s => s.Count <= QueueBacktestsCommand.MaxSymbols)
            .WithMessage($"At most {QueueBacktestsCommand.MaxSymbols} symbols at once.");
        RuleForEach(c => c.Symbols).MustBeSymbol();

        // Strategy, period, capital and parameters are the same for every run: check them once, up front,
        // with the same rules a single run uses.
        RuleFor(c => c).CustomAsync(async (command, context, cancellationToken) =>
        {
            if (command.Symbols.Count == 0 || !command.Symbols.All(Domain.MarketData.Symbol.IsValid))
                return;

            var result = await runValidator.ValidateAsync(command.ForSymbol(command.Symbols[0]), cancellationToken);
            foreach (var failure in result.Errors)
                context.AddFailure(failure);
        });
    }
}

internal sealed class QueueBacktestsCommandHandler(IJobQueue jobs) : ICommandHandler<QueueBacktestsCommand, QueuedBacktests>
{
    public Task<Result<QueuedBacktests>> HandleAsync(QueueBacktestsCommand command, CancellationToken cancellationToken)
    {
        var queued = new List<Guid>();
        var alreadyQueued = new List<string>();

        foreach (var symbol in command.Symbols.Select(s => Symbol.Create(s).Value).Distinct())
        {
            var run = command.ForSymbol(symbol);
            var title = $"Backtest {command.StrategyName} {symbol} {command.Start:yyyy-MM-dd}..{command.End:yyyy-MM-dd}";

            // One active run per symbol and strategy: run ids are timestamp-based and must stay unique.
            var result = jobs.Enqueue(title, context => RunAsync(run, context), $"backtest:{symbol}:{command.StrategyName}");
            if (result.WasQueued)
                queued.Add(result.Job.Id);
            else
                alreadyQueued.Add(symbol);
        }

        return Task.FromResult(Result<QueuedBacktests>.Success(new QueuedBacktests(queued, alreadyQueued)));
    }

    private static async Task<JobOutcome> RunAsync(RunBacktestCommand run, JobExecutionContext context)
    {
        var progress = new DelegateProgress<BacktestProgress>(p => context.Progress.Report(new JobProgress(p.Percent, p.Message)));
        var result = await context.Dispatcher.SendAsync(run with { Progress = progress }, context.CancellationToken);

        return result.Match(r => JobOutcome.Succeeded($"Run {r.RunId} completed."), error => JobOutcome.Failed(error.Message));
    }
}
