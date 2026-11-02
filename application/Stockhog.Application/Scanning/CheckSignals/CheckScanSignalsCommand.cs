using Stockhog.Application.Abstractions.Jobs;
using Stockhog.Application.Abstractions.Messaging;
using Stockhog.Application.Abstractions.Results;
using Stockhog.Application.Signals.EvaluateSignal;
using Stockhog.Domain.Scanning;
using Stockhog.Domain.Signals;
using FluentValidation;

namespace Stockhog.Application.Scanning.CheckSignals;

/// <summary>
/// Asks every candidate of a scan that passed (or every evaluated one) what its strategy wants to do today, as one
/// background job: a signal per pair, run a few at a time and stored on the scan as each arrives.
/// </summary>
public sealed record CheckScanSignalsCommand(string ScanId, bool OnlyPassed = true) : ICommand<SignalCheckQueued>;

/// <param name="JobId">The queued job, or the one already running for this scan.</param>
public sealed record SignalCheckQueued(Guid JobId, int Candidates, bool WasQueued);

/// <summary>Stores a candidate's signal on its scan; sent by the signal check job.</summary>
public sealed record RecordScanSignalCommand(string ScanId, int CandidateIndex, StrategySignal Signal) : ICommand<bool>;

internal sealed class CheckScanSignalsCommandValidator : AbstractValidator<CheckScanSignalsCommand>
{
    public CheckScanSignalsCommandValidator() =>
        RuleFor(c => c.ScanId).Must(ScanRunId.IsValid).WithMessage("'{PropertyValue}' is not a valid scan id.");
}

internal sealed class RecordScanSignalCommandValidator : AbstractValidator<RecordScanSignalCommand>
{
    public RecordScanSignalCommandValidator()
    {
        RuleFor(c => c.ScanId).Must(ScanRunId.IsValid).WithMessage("'{PropertyValue}' is not a valid scan id.");
        RuleFor(c => c.Signal).NotNull();
    }
}

internal sealed class CheckScanSignalsCommandHandler(IScanReader scans, IJobQueue jobs) : ICommandHandler<CheckScanSignalsCommand, SignalCheckQueued>
{
    public async Task<Result<SignalCheckQueued>> HandleAsync(CheckScanSignalsCommand command, CancellationToken cancellationToken)
    {
        if (await scans.FindAsync(ScanRunId.From(command.ScanId), cancellationToken) is not { } scan)
            return ScanErrors.NotFound(command.ScanId);

        var candidates = scan.Candidates
            .Where(c => c.Status == CandidateStatus.Evaluated && (c.Passed || !command.OnlyPassed))
            .Select(c => (c.Index, Symbol: c.Symbol.Value, Strategy: c.Strategy.Value))
            .ToList();
        if (candidates.Count == 0)
            return ScanErrors.NothingToCheck(command.ScanId);

        var scanId = scan.Id.Value;
        var maxParallel = scan.MaxParallel;
        var job = jobs.Enqueue(
            $"Signals for {candidates.Count} candidates of {scanId}",
            context => RunAsync(scanId, candidates, maxParallel, context),
            ScanKeys.SignalsJobKeyPrefix + scanId);

        return new SignalCheckQueued(job.Job.Id, candidates.Count, job.WasQueued);
    }

    /// <summary>A candidate whose signal cannot be read is skipped and counted; the others go on.</summary>
    private static async Task<JobOutcome> RunAsync(
        string scanId,
        IReadOnlyList<(int Index, string Symbol, string Strategy)> candidates,
        int maxParallel,
        JobExecutionContext context)
    {
        var done = 0;
        var failed = 0;
        var kinds = new int[Enum.GetValues<SignalKind>().Length];
        context.Progress.Report(new JobProgress(0, $"0/{candidates.Count} signals"));

        var parallel = new ParallelOptions { MaxDegreeOfParallelism = maxParallel, CancellationToken = context.CancellationToken };
        await Parallel.ForEachAsync(candidates, parallel, async (candidate, token) =>
        {
            var signal = await context.Dispatcher.SendAsync(new EvaluateSignalCommand(candidate.Strategy, candidate.Symbol), token);
            var recorded = signal.IsSuccess
                && (await context.Dispatcher.SendAsync(new RecordScanSignalCommand(scanId, candidate.Index, signal.Value), token)).IsSuccess;

            if (recorded)
                Interlocked.Increment(ref kinds[(int)signal.Value.Kind]);
            else
                Interlocked.Increment(ref failed);

            var finished = Interlocked.Increment(ref done);
            context.Progress.Report(new JobProgress(finished * 100 / candidates.Count, $"{finished}/{candidates.Count} signals"));
        });

        var summary = $"{kinds[(int)SignalKind.Entry]} entries, {kinds[(int)SignalKind.Exit]} exits, {kinds[(int)SignalKind.Holding]} holding, " +
                      $"{kinds[(int)SignalKind.Flat]} flat" + (failed > 0 ? $", {failed} could not be read." : ".");
        return failed == candidates.Count ? JobOutcome.Failed(summary) : JobOutcome.Succeeded(summary);
    }
}

internal sealed class RecordScanSignalCommandHandler(IScanRunRepository repository, TimeProvider clock) : ICommandHandler<RecordScanSignalCommand, bool>
{
    public async Task<Result<bool>> HandleAsync(RecordScanSignalCommand command, CancellationToken cancellationToken)
    {
        if (await repository.GetAsync(ScanRunId.From(command.ScanId), cancellationToken) is not { } scan)
            return ScanErrors.NotFound(command.ScanId);

        scan.RecordSignal(command.CandidateIndex, command.Signal, clock.GetUtcNow());

        // Saved by UnitOfWorkBehavior.
        return true;
    }
}
