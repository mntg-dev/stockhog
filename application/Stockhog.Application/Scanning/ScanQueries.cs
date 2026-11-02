using Stockhog.Application.Abstractions.Messaging;
using Stockhog.Application.Abstractions.Results;
using Stockhog.Domain.Scanning;
using FluentValidation;

namespace Stockhog.Application.Scanning;

public sealed record ScanSummary(
    string Id,
    DateTimeOffset CreatedAt,
    ScanStatus Status,
    DateOnly Start,
    DateOnly End,
    int Symbols,
    int Strategies,
    int Candidates,
    int Finished,
    int Passed,
    string? FailureReason)
{
    public static ScanSummary From(ScanRun run) => new(
        run.Id.Value,
        run.CreatedAt,
        run.Status,
        run.Period.Start,
        run.Period.End,
        run.Symbols.Count,
        run.Strategies.Count,
        run.Candidates.Count,
        run.FinishedCandidates,
        run.PassedCandidates,
        run.FailureReason);
}

/// <summary>One candidate as the scanner page shows it; the evaluation figures are null until it is evaluated.</summary>
public sealed record ScanCandidateRow(
    int Index,
    string Symbol,
    string StrategyName,
    CandidateStatus Status,
    string? RunId,
    bool Passed,
    decimal? Score,
    decimal? NetProfitPercent,
    decimal? MaxDrawdownPercent,
    decimal? MonteCarloDrawdownPercent,
    decimal? FirstHalfReturnPercent,
    decimal? SecondHalfReturnPercent,
    decimal? BuyAndHoldReturnPercent,
    decimal? ExcessOverBuyAndHoldPercent,
    decimal? SharpeRatio,
    int? TotalTrades,
    decimal? WinRatePercent,
    ScanRejection Rejections,
    string? FailureReason)
{
    public static ScanCandidateRow From(ScanCandidate candidate)
    {
        var evaluation = candidate.Evaluation;
        var metrics = evaluation?.Metrics;
        return new ScanCandidateRow(
            candidate.Index,
            candidate.Symbol.Value,
            candidate.Strategy.Value,
            candidate.Status,
            candidate.RunId?.Value,
            candidate.Passed,
            evaluation?.Score,
            metrics?.NetProfitPercent,
            metrics?.MaxDrawdownPercent,
            evaluation?.MonteCarloDrawdownPercent,
            evaluation?.FirstHalfReturnPercent,
            evaluation?.SecondHalfReturnPercent,
            evaluation?.BuyAndHoldReturnPercent,
            evaluation?.ExcessOverBuyAndHoldPercent,
            metrics?.SharpeRatio,
            metrics?.TotalTrades,
            metrics?.WinRatePercent,
            evaluation?.Rejections ?? ScanRejection.None,
            candidate.FailureReason);
    }
}

/// <summary>A scan with its candidates: passed first by score, then the rejected by score, then the rest.</summary>
public sealed record ScanDetails(ScanSummary Summary, ScanThresholds Thresholds, decimal InitialCapital, IReadOnlyList<ScanCandidateRow> Candidates);

public sealed record GetScansQuery(int Take = 20) : IQuery<IReadOnlyList<ScanSummary>>;

public sealed record GetScanQuery(string ScanId) : IQuery<ScanDetails>;

internal sealed class GetScansQueryValidator : AbstractValidator<GetScansQuery>
{
    public GetScansQueryValidator() => RuleFor(q => q.Take).InclusiveBetween(1, 100);
}

internal sealed class GetScanQueryValidator : AbstractValidator<GetScanQuery>
{
    public GetScanQueryValidator() => RuleFor(q => q.ScanId).Must(ScanRunId.IsValid).WithMessage("'{PropertyValue}' is not a valid scan id.");
}

internal sealed class GetScansQueryHandler(IScanReader reader) : IQueryHandler<GetScansQuery, IReadOnlyList<ScanSummary>>
{
    public async Task<Result<IReadOnlyList<ScanSummary>>> HandleAsync(GetScansQuery query, CancellationToken cancellationToken) =>
        Result<IReadOnlyList<ScanSummary>>.Success((await reader.ListAsync(query.Take, cancellationToken)).Select(ScanSummary.From).ToList());
}

internal sealed class GetScanQueryHandler(IScanReader reader) : IQueryHandler<GetScanQuery, ScanDetails>
{
    public async Task<Result<ScanDetails>> HandleAsync(GetScanQuery query, CancellationToken cancellationToken)
    {
        if (await reader.FindAsync(ScanRunId.From(query.ScanId), cancellationToken) is not { } run)
            return ScanErrors.NotFound(query.ScanId);

        var candidates = run.Candidates
            .Select(ScanCandidateRow.From)
            .OrderByDescending(c => c.Passed)
            .ThenByDescending(c => c.Score.HasValue)
            .ThenByDescending(c => c.Score)
            .ThenBy(c => c.Index)
            .ToList();

        return new ScanDetails(ScanSummary.From(run), run.Thresholds, run.InitialCapital.Amount, candidates);
    }
}

/// <summary>
/// Sent at startup. A scan still Queued or Running belongs to a previous session that ended before it finished, so it
/// is marked as failed; its evaluated candidates are kept.
/// </summary>
public sealed record FailInterruptedScansCommand : ICommand<int>;

internal sealed class FailInterruptedScansCommandHandler(IScanRunRepository repository, TimeProvider clock)
    : ICommandHandler<FailInterruptedScansCommand, int>
{
    public const string Reason = "Interrupted: the application closed before the scan finished.";

    public async Task<Result<int>> HandleAsync(FailInterruptedScansCommand command, CancellationToken cancellationToken)
    {
        var unfinished = await repository.ListUnfinishedAsync(cancellationToken);
        foreach (var run in unfinished)
            run.Fail(Reason, clock.GetUtcNow());

        // Saved by UnitOfWorkBehavior.
        return unfinished.Count;
    }
}
