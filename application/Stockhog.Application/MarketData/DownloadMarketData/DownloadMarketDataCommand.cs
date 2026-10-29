using Stockhog.Application.Abstractions.Messaging;
using Stockhog.Application.Abstractions.Results;
using Stockhog.Application.Common;
using Stockhog.Domain.Common;
using Stockhog.Domain.MarketData;
using FluentValidation;

namespace Stockhog.Application.MarketData.DownloadMarketData;

/// <summary>
/// Brings local data for one symbol and timeframe up to date for the period, downloading only days not stored yet.
/// </summary>
/// <param name="ForceRefresh">Download the whole period even if it is already stored.</param>
public sealed record DownloadMarketDataCommand(string Symbol, Timeframe Timeframe, DateOnly Start, DateOnly End, bool ForceRefresh = false)
    : ICommand<MarketDataUpdateResult>
{
    /// <summary>Optional live progress (0 to 100) for the caller; not part of the command's data.</summary>
    public IProgress<int>? Progress { get; init; }
}

public sealed record MarketDataUpdateResult(string Symbol, Timeframe Timeframe, int RangesDownloaded, int BarCount)
{
    public bool WasUpToDate => RangesDownloaded == 0;

    public string Describe() => WasUpToDate
        ? $"{Symbol} {Timeframe}: already up to date."
        : $"{Symbol} {Timeframe}: {BarCount:N0} bars in {RangesDownloaded} range(s).";
}

internal sealed class DownloadMarketDataCommandValidator : AbstractValidator<DownloadMarketDataCommand>
{
    public DownloadMarketDataCommandValidator(TimeProvider clock)
    {
        RuleFor(c => c.Symbol).MustBeSymbol();
        RuleFor(c => c.Timeframe).IsInEnum();
        RuleFor(c => c.Start).MustNotBeInTheFuture(clock);
        RuleFor(c => c.End).GreaterThanOrEqualTo(c => c.Start).WithMessage("End date must not be before start date.");
    }
}

internal sealed class DownloadMarketDataCommandHandler(
    IMarketDataDownloader downloader,
    IMarketDataSetRepository sets,
    TimeProvider clock) : ICommandHandler<DownloadMarketDataCommand, MarketDataUpdateResult>
{
    public async Task<Result<MarketDataUpdateResult>> HandleAsync(DownloadMarketDataCommand command, CancellationToken cancellationToken)
    {
        var symbol = Symbol.Create(command.Symbol);
        var period = DateRange.Create(command.Start, command.End);

        var existing = await sets.GetAsync(MarketDataSetId.For(symbol, command.Timeframe), cancellationToken);
        var set = existing ?? MarketDataSet.Create(symbol, command.Timeframe);

        IReadOnlyList<DateRange> missing = command.ForceRefresh ? [period] : set.MissingRanges(period);
        if (missing.Count == 0)
        {
            command.Progress?.Report(100);
            return new MarketDataUpdateResult(symbol.Value, command.Timeframe, 0, 0);
        }

        var completeThrough = ExchangeCalendar.LastCompletedSession(clock);
        var barCount = 0;

        for (var i = 0; i < missing.Count; i++)
        {
            var request = new MarketDataRequest(symbol, command.Timeframe, missing[i]);
            var downloaded = await downloader.DownloadAsync(request, ScaledProgress.ForPart(command.Progress, i, missing.Count), cancellationToken);

            barCount += downloaded.BarCount;
            set.RecordDownload(missing[i], completeThrough, downloaded.BarCount, clock.GetUtcNow());
        }

        // A symbol that never returned any data is most likely mistyped: report it and store nothing.
        if (existing is null && barCount == 0)
            return MarketDataErrors.NoData(symbol, command.Timeframe, period);

        if (existing is null)
            sets.Add(set);

        // Saved by UnitOfWorkBehavior.
        return new MarketDataUpdateResult(symbol.Value, command.Timeframe, missing.Count, barCount);
    }
}

public static class MarketDataErrors
{
    public static Error NoData(Symbol symbol, Timeframe timeframe, DateRange period) =>
        Error.NotFound("MarketData.NoData", $"No {timeframe} data for {symbol} in {period}. Check the symbol.");
}
