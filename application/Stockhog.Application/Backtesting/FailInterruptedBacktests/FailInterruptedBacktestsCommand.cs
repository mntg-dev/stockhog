using Stockhog.Application.Abstractions.Messaging;
using Stockhog.Application.Abstractions.Results;

namespace Stockhog.Application.Backtesting.FailInterruptedBacktests;

/// <summary>
/// Sent at startup. A run that is still Queued or Running belongs to a previous session that ended
/// before the run finished, so it is marked as failed instead of staying "Running" forever.
/// </summary>
/// <returns>The number of runs marked as failed.</returns>
public sealed record FailInterruptedBacktestsCommand : ICommand<int>;

internal sealed class FailInterruptedBacktestsCommandHandler(IBacktestRunRepository repository, TimeProvider clock)
    : ICommandHandler<FailInterruptedBacktestsCommand, int>
{
    public const string Reason = "Interrupted: the application closed before the run finished.";

    public async Task<Result<int>> HandleAsync(FailInterruptedBacktestsCommand command, CancellationToken cancellationToken)
    {
        var unfinished = await repository.ListUnfinishedAsync(cancellationToken);
        foreach (var run in unfinished)
            run.Fail(Reason, clock.GetUtcNow());

        // Saved by UnitOfWorkBehavior.
        return unfinished.Count;
    }
}
