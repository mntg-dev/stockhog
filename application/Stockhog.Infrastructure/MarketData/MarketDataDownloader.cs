using System.Runtime.ExceptionServices;
using System.Threading.Channels;
using Stockhog.Application.MarketData;
using Microsoft.Extensions.Logging;

namespace Stockhog.Infrastructure.MarketData;

/// <summary>
/// Producer/consumer pipeline: one task downloads pages from the bar source into a bounded channel while another writes
/// them to disk, so network and disk work overlap. The bounded channel applies backpressure: if writing falls
/// behind, downloading waits instead of buffering everything in memory.
/// </summary>
internal sealed class MarketDataDownloader(
    IHistoricalBarSource source,
    IMarketDataStore store,
    TimeProvider clock,
    ILogger<MarketDataDownloader> logger) : IMarketDataDownloader
{
    private const int PagesInFlight = 4;

    // Share of the progress bar given to each stage; downloading dominates.
    private const int ReferenceDataEnd = 5;
    private const int DownloadEnd = 95;

    public async Task<MarketDataDownloadResult> DownloadAsync(
        MarketDataRequest request,
        IProgress<int>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var symbol = request.Symbol.Value;

        progress?.Report(0);
        await store.EnsureReferenceDataAsync(cancellationToken).ConfigureAwait(false);
        progress?.Report(ReferenceDataEnd);

        var startUtc = request.Period.Start.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
        var endOfLastDayUtc = request.Period.End.AddDays(1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
        var nowUtc = clock.GetUtcNow().UtcDateTime;
        var endUtc = endOfLastDayUtc < nowUtc ? endOfLastDayUtc : nowUtc;

        logger.LogInformation("Downloading {Timeframe} bars for {Symbol} in {Period}", request.Timeframe, symbol, request.Period);

        var pages = Channel.CreateBounded<IReadOnlyList<MarketBar>>(new BoundedChannelOptions(PagesInFlight)
        {
            SingleReader = true,
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.Wait
        });

        // If either side fails, the other is cancelled so neither waits forever on the channel.
        using var pipeline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        var producer = Task.Run(async () =>
        {
            try
            {
                var fetchProgress = progress is null ? null : new ScaledProgress(progress, ReferenceDataEnd, DownloadEnd);
                await foreach (var page in source.StreamPagesAsync(symbol, request.Timeframe, startUtc, endUtc, fetchProgress, pipeline.Token))
                    await pages.Writer.WriteAsync(page, pipeline.Token);

                pages.Writer.Complete();
            }
            catch (Exception ex)
            {
                pages.Writer.Complete(ex);
                pipeline.Cancel();
                throw;
            }
        }, CancellationToken.None);

        var consumer = Task.Run(async () =>
        {
            try
            {
                var sink = store.CreateSink(symbol, request.Timeframe);
                var barCount = 0;
                await foreach (var page in pages.Reader.ReadAllAsync(pipeline.Token))
                {
                    await sink.AddAsync(page, pipeline.Token);
                    barCount += page.Count;
                }

                return (BarCount: barCount, Files: await sink.CompleteAsync(pipeline.Token));
            }
            catch
            {
                pipeline.Cancel();
                throw;
            }
        }, CancellationToken.None);

        try
        {
            await Task.WhenAll(producer, consumer).ConfigureAwait(false);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            // The side that failed first cancels the other, which then fails too. Surface the real error,
            // not the cancellation it caused.
            var rootCause = new[] { producer, consumer }
                .Where(t => t.IsFaulted)
                .SelectMany(t => t.Exception!.InnerExceptions)
                .FirstOrDefault(e => e is not OperationCanceledException);

            if (rootCause is not null)
                ExceptionDispatchInfo.Throw(rootCause);

            throw;
        }

        var (bars, files) = await consumer.ConfigureAwait(false);

        logger.LogInformation("Stored {BarCount} bars for {Symbol} in {FileCount} file(s)", bars, symbol, files);
        progress?.Report(100);
        return new MarketDataDownloadResult(symbol, bars, files);
    }

    // Reports synchronously; Progress<T> would post each value separately and could deliver them out of order.
    private sealed class ScaledProgress(IProgress<int> inner, int from, int to) : IProgress<double>
    {
        public void Report(double value) => inner.Report(from + (int)Math.Round((to - from) * value));
    }
}
