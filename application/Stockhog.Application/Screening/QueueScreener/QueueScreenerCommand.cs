using Stockhog.Application.Abstractions.Jobs;
using Stockhog.Application.Abstractions.Messaging;
using Stockhog.Application.Abstractions.Results;
using Stockhog.Application.Screening.RunScreener;
using FluentValidation;

namespace Stockhog.Application.Screening.QueueScreener;

/// <summary>
/// Runs the screener as a background job. Only one run at a time: a second request returns the running job.
/// </summary>
public sealed record QueueScreenerCommand(ScreenerCriteria Criteria) : ICommand<QueuedScreener>;

/// <param name="JobId">The queued job, or the one already running.</param>
public sealed record QueuedScreener(Guid JobId, bool WasQueued);

internal sealed class QueueScreenerCommandValidator : AbstractValidator<QueueScreenerCommand>
{
    public QueueScreenerCommandValidator() => RuleFor(c => c.Criteria).NotNull().SetValidator(new ScreenerCriteriaValidator());
}

internal sealed class QueueScreenerCommandHandler(IJobQueue jobs, IDailyBarSource bars) : ICommandHandler<QueueScreenerCommand, QueuedScreener>
{
    public Task<Result<QueuedScreener>> HandleAsync(QueueScreenerCommand command, CancellationToken cancellationToken)
    {
        // Fail now rather than as a failed job, as the instrument refresh does.
        if (!bars.IsConfigured)
            return Task.FromResult<Result<QueuedScreener>>(ScreenerErrors.SourceNotConfigured);

        var run = new RunScreenerCommand(command.Criteria);
        var result = jobs.Enqueue("Screener", context => RunAsync(run, context), ScreenerKeys.RunJobKey);
        return Task.FromResult(Result<QueuedScreener>.Success(new QueuedScreener(result.Job.Id, result.WasQueued)));
    }

    private static async Task<JobOutcome> RunAsync(RunScreenerCommand run, JobExecutionContext context)
    {
        var result = await context.Dispatcher.SendAsync(run with { Progress = context.Progress }, context.CancellationToken);
        return result.Match(
            s => JobOutcome.Succeeded($"{s.Matched} of {s.WithData} instruments match (data as of {s.AsOf:yyyy-MM-dd})."),
            error => JobOutcome.Failed(error.Message));
    }
}
