using System.Globalization;
using System.IO.Compression;
using System.Text;
using Stockhog.Domain.MarketData;
using Stockhog.Infrastructure.MarketData;

namespace Stockhog.Infrastructure.Lean.Data;

/// <summary>
/// Writes US equity trade bars in the Lean local data format.
/// Prices are stored as integers in 1/10000 of a dollar, times in exchange time.
/// See https://github.com/QuantConnect/Lean/tree/master/Data/equity
/// </summary>
internal sealed class LeanEquityDataWriter
{
    private const decimal PriceScale = 10_000m;

    /// <returns>The number of files written.</returns>
    public Task<int> WriteAsync(
        string dataFolder,
        string symbol,
        Timeframe timeframe,
        IReadOnlyList<MarketBar> bars,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var ticker = symbol.Trim().ToLowerInvariant();
        return timeframe switch
        {
            Timeframe.Minute => WriteMinuteAsync(dataFolder, ticker, bars, progress, cancellationToken),
            Timeframe.Hour or Timeframe.Daily => WriteSingleFileAsync(dataFolder, ticker, timeframe, bars, progress, cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(timeframe), timeframe, null)
        };
    }

    internal static string GetFilePath(string dataFolder, string ticker, Timeframe timeframe, DateOnly? day = null) => timeframe switch
    {
        Timeframe.Minute => Path.Combine(dataFolder, "equity", "usa", "minute", ticker, $"{day:yyyyMMdd}_trade.zip"),
        Timeframe.Hour => Path.Combine(dataFolder, "equity", "usa", "hour", $"{ticker}.zip"),
        Timeframe.Daily => Path.Combine(dataFolder, "equity", "usa", "daily", $"{ticker}.zip"),
        _ => throw new ArgumentOutOfRangeException(nameof(timeframe), timeframe, null)
    };

    /// <summary>
    /// One zip per trading day; a day that is downloaded again is replaced.
    /// </summary>
    private static async Task<int> WriteMinuteAsync(
        string dataFolder, string ticker, IReadOnlyList<MarketBar> bars, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var days = bars
            .Select(bar => (Time: ExchangeTime.FromUtc(bar.TimeUtc), Bar: bar))
            .GroupBy(x => DateOnly.FromDateTime(x.Time))
            .OrderBy(g => g.Key)
            .ToList();

        for (var i = 0; i < days.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await WriteMinuteDayAsync(dataFolder, ticker, days[i].Key, days[i].ToList(), cancellationToken).ConfigureAwait(false);
            progress?.Report((i + 1) / (double)days.Count);
        }

        return days.Count;
    }

    internal static async Task WriteMinuteDayAsync(
        string dataFolder, string ticker, DateOnly day, IReadOnlyList<(DateTime Time, MarketBar Bar)> bars, CancellationToken cancellationToken)
    {
        var csv = new StringBuilder();
        foreach (var (time, bar) in bars.OrderBy(x => x.Time))
        {
            var millisecondsSinceMidnight = (long)time.TimeOfDay.TotalMilliseconds;
            csv.Append(millisecondsSinceMidnight.ToString(CultureInfo.InvariantCulture)).Append(',');
            AppendOhlcv(csv, bar);
        }

        var path = GetFilePath(dataFolder, ticker, Timeframe.Minute, day);
        await WriteZipAsync(path, $"{day:yyyyMMdd}_{ticker}_minute_trade.csv", csv.ToString(), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Creates a sink that accepts bars page by page (oldest first) and writes them as soon as possible.
    /// </summary>
    public IBarSink CreateSink(string dataFolder, string symbol, Timeframe timeframe)
    {
        var ticker = symbol.Trim().ToLowerInvariant();
        return timeframe switch
        {
            Timeframe.Minute => new MinuteBarSink(dataFolder, ticker),
            Timeframe.Hour or Timeframe.Daily => new SingleFileBarSink(dataFolder, ticker, timeframe),
            _ => throw new ArgumentOutOfRangeException(nameof(timeframe), timeframe, null)
        };
    }

    /// <summary>
    /// Writes each exchange day as soon as a bar from a later day arrives, so files are written while
    /// later pages are still downloading. Relies on pages arriving oldest first, as Alpaca returns them.
    /// </summary>
    private sealed class MinuteBarSink(string dataFolder, string ticker) : IBarSink
    {
        private readonly SortedDictionary<DateOnly, List<(DateTime Time, MarketBar Bar)>> _pendingDays = [];

        public int FilesWritten { get; private set; }

        public async Task AddAsync(IReadOnlyList<MarketBar> bars, CancellationToken cancellationToken)
        {
            foreach (var bar in bars)
            {
                var time = ExchangeTime.FromUtc(bar.TimeUtc);
                var day = DateOnly.FromDateTime(time);
                if (!_pendingDays.TryGetValue(day, out var dayBars))
                    _pendingDays[day] = dayBars = [];

                dayBars.Add((time, bar));
            }

            // Every day before the newest one is complete.
            while (_pendingDays.Count > 1)
                await FlushOldestAsync(cancellationToken).ConfigureAwait(false);
        }

        public async Task<int> CompleteAsync(CancellationToken cancellationToken)
        {
            while (_pendingDays.Count > 0)
                await FlushOldestAsync(cancellationToken).ConfigureAwait(false);

            return FilesWritten;
        }

        private async Task FlushOldestAsync(CancellationToken cancellationToken)
        {
            var (day, bars) = _pendingDays.First();
            _pendingDays.Remove(day);
            await WriteMinuteDayAsync(dataFolder, ticker, day, bars, cancellationToken).ConfigureAwait(false);
            FilesWritten++;
        }
    }

    /// <summary>
    /// Hour and daily data share one file per symbol, so bars are collected and merged into it at the end.
    /// </summary>
    private sealed class SingleFileBarSink(string dataFolder, string ticker, Timeframe timeframe) : IBarSink
    {
        private readonly List<MarketBar> _bars = [];

        public Task AddAsync(IReadOnlyList<MarketBar> bars, CancellationToken cancellationToken)
        {
            _bars.AddRange(bars);
            return Task.CompletedTask;
        }

        public Task<int> CompleteAsync(CancellationToken cancellationToken) =>
            _bars.Count == 0 ? Task.FromResult(0) : WriteSingleFileAsync(dataFolder, ticker, timeframe, _bars, null, cancellationToken);
    }

    /// <summary>
    /// Hour and daily data live in one zip per symbol, so new bars are merged with the rows already stored.
    /// </summary>
    private static async Task<int> WriteSingleFileAsync(
        string dataFolder, string ticker, Timeframe timeframe, IReadOnlyList<MarketBar> bars, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var path = GetFilePath(dataFolder, ticker, timeframe);
        var entryName = $"{ticker}.csv";

        var rows = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in await ReadZipLinesAsync(path, entryName, cancellationToken).ConfigureAwait(false))
        {
            var separator = line.IndexOf(',');
            if (separator > 0)
                rows[line[..separator]] = line;
        }

        foreach (var bar in bars)
        {
            var time = ExchangeTime.FromUtc(bar.TimeUtc);
            var key = timeframe == Timeframe.Daily
                ? time.ToString("yyyyMMdd 00:00", CultureInfo.InvariantCulture)
                : time.ToString("yyyyMMdd HH:mm", CultureInfo.InvariantCulture);

            var row = new StringBuilder(key).Append(',');
            AppendOhlcv(row, bar);
            rows[key] = row.ToString().TrimEnd('\n');
        }

        await WriteZipAsync(path, entryName, string.Join('\n', rows.Values) + "\n", cancellationToken).ConfigureAwait(false);
        progress?.Report(1);
        return 1;
    }

    private static void AppendOhlcv(StringBuilder csv, MarketBar bar)
    {
        csv.Append(Scale(bar.Open)).Append(',')
           .Append(Scale(bar.High)).Append(',')
           .Append(Scale(bar.Low)).Append(',')
           .Append(Scale(bar.Close)).Append(',')
           .Append(bar.Volume.ToString(CultureInfo.InvariantCulture)).Append('\n');
    }

    private static string Scale(decimal price) =>
        ((long)Math.Round(price * PriceScale, MidpointRounding.AwayFromZero)).ToString(CultureInfo.InvariantCulture);

    private static async Task<IReadOnlyList<string>> ReadZipLinesAsync(string path, string entryName, CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
            return [];

        using var archive = ZipFile.OpenRead(path);
        var entry = archive.GetEntry(entryName);
        if (entry is null)
            return [];

        using var reader = new StreamReader(entry.Open());
        var content = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        return content.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    /// <summary>
    /// Writes to a temporary file first so a cancelled download never leaves a corrupt zip behind.
    /// </summary>
    private static async Task WriteZipAsync(string path, string entryName, string content, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tempPath = path + ".tmp";

        await using (var file = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
        using (var archive = new ZipArchive(file, ZipArchiveMode.Create))
        {
            var entry = archive.CreateEntry(entryName, CompressionLevel.Optimal);
            await using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            await writer.WriteAsync(content.AsMemory(), cancellationToken).ConfigureAwait(false);
        }

        File.Move(tempPath, path, overwrite: true);
    }
}
