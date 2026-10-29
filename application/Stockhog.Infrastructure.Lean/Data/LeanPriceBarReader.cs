using System.Collections.Concurrent;
using System.Globalization;
using System.IO.Compression;
using Stockhog.Application.MarketData.GetPriceBars;
using Stockhog.Domain.Common;
using Stockhog.Domain.MarketData;
using Stockhog.Infrastructure.Configuration;
using Microsoft.Extensions.Options;

namespace Stockhog.Infrastructure.Lean.Data;

/// <summary>
/// Reads bars back from the Lean data folder. Uses the stored file of the requested resolution when it exists;
/// otherwise aggregates minute data, reading the per-day minute files in parallel (a year is about 250 zips).
/// </summary>
internal sealed class LeanPriceBarReader(IOptions<StorageOptions> storage) : IPriceBarReader
{
    private const decimal PriceScale = 10_000m;

    public async Task<IReadOnlyList<PriceBarData>> ReadAsync(
        Symbol symbol, BarResolution resolution, DateRange period, CancellationToken cancellationToken = default)
    {
        var dataFolder = storage.Value.DataFolder;
        var ticker = symbol.Value.ToLowerInvariant();

        var storedTimeframe = resolution == BarResolution.Daily ? Timeframe.Daily : Timeframe.Hour;
        var storedFile = LeanEquityDataWriter.GetFilePath(dataFolder, ticker, storedTimeframe);
        if (File.Exists(storedFile))
            return await ReadSingleFileAsync(storedFile, ticker, period, cancellationToken).ConfigureAwait(false);

        return await AggregateMinuteDataAsync(dataFolder, ticker, resolution, period, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<IReadOnlyList<PriceBarData>> ReadSingleFileAsync(
        string path, string ticker, DateRange period, CancellationToken cancellationToken)
    {
        var bars = new List<PriceBarData>();
        foreach (var line in await ReadEntryLinesAsync(path, $"{ticker}.csv", cancellationToken).ConfigureAwait(false))
        {
            // yyyyMMdd HH:mm,open,high,low,close,volume
            var fields = line.Split(',');
            var time = DateTime.ParseExact(fields[0], "yyyyMMdd HH:mm", CultureInfo.InvariantCulture);
            if (period.Contains(DateOnly.FromDateTime(time)))
                bars.Add(Parse(time, fields));
        }

        return bars;
    }

    private static async Task<IReadOnlyList<PriceBarData>> AggregateMinuteDataAsync(
        string dataFolder, string ticker, BarResolution resolution, DateRange period, CancellationToken cancellationToken)
    {
        var folder = Path.GetDirectoryName(LeanEquityDataWriter.GetFilePath(dataFolder, ticker, Timeframe.Minute, period.Start))!;
        if (!Directory.Exists(folder))
            return [];

        var days = Directory.EnumerateFiles(folder, "*_trade.zip")
            .Select(path => (Path: path, Day: DateOnly.ParseExact(Path.GetFileName(path)[..8], "yyyyMMdd", CultureInfo.InvariantCulture)))
            .Where(file => period.Contains(file.Day))
            .ToList();

        var bars = new ConcurrentBag<PriceBarData>();
        var parallel = new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount, CancellationToken = cancellationToken };

        await Parallel.ForEachAsync(days, parallel, async (file, ct) =>
        {
            var minutes = await ReadMinuteDayAsync(file.Path, ticker, file.Day, ct).ConfigureAwait(false);
            var groups = resolution == BarResolution.Daily
                ? minutes.GroupBy(m => file.Day.ToDateTime(TimeOnly.MinValue))
                : minutes.GroupBy(m => new DateTime(m.Time.Year, m.Time.Month, m.Time.Day, m.Time.Hour, 0, 0));

            foreach (var group in groups)
                bars.Add(Aggregate(group.Key, group.ToList()));
        }).ConfigureAwait(false);

        return bars.OrderBy(b => b.Time).ToList();
    }

    private static async Task<List<PriceBarData>> ReadMinuteDayAsync(string path, string ticker, DateOnly day, CancellationToken cancellationToken)
    {
        var midnight = day.ToDateTime(TimeOnly.MinValue);
        var entry = $"{day:yyyyMMdd}_{ticker}_minute_trade.csv";

        // milliseconds since midnight (exchange time),open,high,low,close,volume
        return (await ReadEntryLinesAsync(path, entry, cancellationToken).ConfigureAwait(false))
            .Select(line => line.Split(','))
            .Select(fields => Parse(midnight.AddMilliseconds(long.Parse(fields[0], CultureInfo.InvariantCulture)), fields))
            .OrderBy(b => b.Time)
            .ToList();
    }

    /// <summary>Bars must be ordered by time.</summary>
    private static PriceBarData Aggregate(DateTime time, IReadOnlyList<PriceBarData> bars) => new(
        time,
        bars[0].Open,
        bars.Max(b => b.High),
        bars.Min(b => b.Low),
        bars[^1].Close,
        bars.Sum(b => b.Volume));

    private static PriceBarData Parse(DateTime time, string[] fields) => new(
        time,
        long.Parse(fields[1], CultureInfo.InvariantCulture) / PriceScale,
        long.Parse(fields[2], CultureInfo.InvariantCulture) / PriceScale,
        long.Parse(fields[3], CultureInfo.InvariantCulture) / PriceScale,
        long.Parse(fields[4], CultureInfo.InvariantCulture) / PriceScale,
        long.Parse(fields[5], CultureInfo.InvariantCulture));

    private static async Task<string[]> ReadEntryLinesAsync(string path, string entryName, CancellationToken cancellationToken)
    {
        using var archive = ZipFile.OpenRead(path);
        var entry = archive.GetEntry(entryName) ?? archive.Entries.FirstOrDefault();
        if (entry is null)
            return [];

        using var reader = new StreamReader(entry.Open());
        var content = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        return content.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }
}
