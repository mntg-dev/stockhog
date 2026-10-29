using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Threading.Channels;
using Stockhog.Application.Trading;
using Stockhog.Domain.MarketData;
using Stockhog.Infrastructure.Alpaca.Streaming;
using Stockhog.Infrastructure.MarketData;
using Stockhog.Infrastructure.Trading;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Stockhog.Infrastructure.Alpaca.MarketData;

/// <summary>
/// Live trades, quotes and minute bars for the watchlist.
/// <para>
/// Producer/consumer: the socket reader only parses messages and writes them to a bounded channel; a separate
/// consumer applies them to the quote cache and the bar series and raises <see cref="Updated"/>. If the consumer
/// falls behind, the oldest events are dropped (a newer quote replaces them anyway) instead of slowing the reader,
/// which would make the server drop the connection.
/// </para>
/// </summary>
internal sealed class AlpacaMarketDataStream(
    IOptions<AlpacaOptions> options,
    StreamSocketFactory socketFactory,
    ConnectionMonitor monitor,
    QuoteCache quotes,
    IHistoricalBarSource history,
    TimeProvider clock,
    ILogger<AlpacaMarketDataStream> logger) : AlpacaStreamService(socketFactory, monitor, clock, logger), ILiveMarketData
{
    public const string StreamName = "Market data";

    /// <summary>The free plan allows 30 symbols per connection.</summary>
    public const int MaxSymbols = 30;

    private const int ChannelCapacity = 10_000;

    /// <summary>Far enough back to reach the previous session over a long weekend.</summary>
    private static readonly TimeSpan SessionLookback = TimeSpan.FromDays(5);
    private static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(15);

    private readonly Channel<MarketEvent> _events = Channel.CreateBounded<MarketEvent>(
        new BoundedChannelOptions(ChannelCapacity) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true, SingleWriter = true });

    private readonly ConcurrentDictionary<string, BarSeries> _bars = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _watchlistLock = new(1, 1);
    private ImmutableSortedSet<string> _watchlist = ImmutableSortedSet<string>.Empty;

    public event EventHandler? Updated;

    public IReadOnlyCollection<string> Watchlist => Volatile.Read(ref _watchlist);

    protected override string Name => StreamName;

    protected override Uri Endpoint => new($"{options.Value.MarketDataStreamUrl.TrimEnd('/')}/{options.Value.Feed}");

    protected override bool HasCredentials => options.Value.HasCredentials;

    public IReadOnlyList<LiveBar> GetBars(Symbol symbol) =>
        _bars.TryGetValue(symbol.Value, out var series) ? series.Snapshot() : [];

    public async Task SetWatchlistAsync(IReadOnlyCollection<Symbol> symbols, CancellationToken cancellationToken = default)
    {
        var wanted = symbols.Select(s => s.Value).Distinct().Take(MaxSymbols).ToImmutableSortedSet(StringComparer.Ordinal);

        // Serialized: two quick changes must not interleave their subscribe and unsubscribe messages.
        await _watchlistLock.WaitAsync(cancellationToken);
        try
        {
            var current = Volatile.Read(ref _watchlist);
            var added = wanted.Except(current).ToList();
            var removed = current.Except(wanted).ToList();
            Volatile.Write(ref _watchlist, wanted);

            foreach (var symbol in removed)
            {
                quotes.Remove(symbol);
                _bars.TryRemove(symbol, out _);
            }

            if (CurrentSocket is { } socket)
            {
                if (removed.Count > 0)
                    await socket.SendAsync(SubscriptionMessage("unsubscribe", removed), cancellationToken);
                if (added.Count > 0)
                    await socket.SendAsync(SubscriptionMessage("subscribe", added), cancellationToken);
            }

            // History for the new symbols in parallel, so the chart has the session so far.
            await Task.WhenAll(added.Select(symbol => BackfillAsync(symbol, cancellationToken)));
        }
        finally
        {
            _watchlistLock.Release();
        }

        Updated?.Invoke(this, EventArgs.Empty);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var consumer = Task.Run(() => ConsumeAsync(stoppingToken), stoppingToken);
        try
        {
            await base.ExecuteAsync(stoppingToken);
        }
        finally
        {
            _events.Writer.TryComplete();
            await consumer.ContinueWith(_ => { }, TaskScheduler.Default);
        }
    }

    protected override async Task HandshakeAsync(IStreamSocket socket, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(HandshakeTimeout);

        await ExpectAsync(socket, "connected", timeout.Token);
        var settings = options.Value;
        await socket.SendAsync(StreamJson.Serialize(new { action = "auth", key = settings.ApiKey, secret = settings.ApiSecret }), timeout.Token);
        await ExpectAsync(socket, "authenticated", timeout.Token);

        if (Watchlist is { Count: > 0 } symbols)
            await socket.SendAsync(SubscriptionMessage("subscribe", symbols), timeout.Token);
    }

    protected override Task OnMessageAsync(string message, CancellationToken cancellationToken)
    {
        var (events, errors) = AlpacaStreamMessages.ParseMarketData(message);
        foreach (var error in errors)
            logger.LogWarning("Market data stream error {Error}", error);

        // DropOldest: TryWrite always succeeds, and the reader never waits for the consumer.
        foreach (var marketEvent in events)
            _events.Writer.TryWrite(marketEvent);

        return Task.CompletedTask;
    }

    /// <summary>Applies everything waiting in the channel, then raises one <see cref="Updated"/> for the batch.</summary>
    private async Task ConsumeAsync(CancellationToken cancellationToken)
    {
        var reader = _events.Reader;
        try
        {
            while (await reader.WaitToReadAsync(cancellationToken))
            {
                while (reader.TryRead(out var marketEvent))
                    Apply(marketEvent);

                Updated?.Invoke(this, EventArgs.Empty);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Shutting down.
        }
    }

    private void Apply(MarketEvent marketEvent)
    {
        // Late events for a symbol just removed from the watchlist are dropped.
        if (!Volatile.Read(ref _watchlist).Contains(marketEvent.Symbol))
            return;

        switch (marketEvent)
        {
            case TradeEvent trade:
                quotes.Update(trade.Symbol, null, null, trade.Price, trade.Time);
                break;
            case QuoteEvent quote:
                quotes.Update(quote.Symbol, quote.Bid, quote.Ask, null, quote.Time);
                break;
            case BarEvent bar:
                _bars.GetOrAdd(bar.Symbol, _ => new BarSeries()).Upsert(bar.Bar);
                break;
        }
    }

    /// <summary>
    /// The latest session's minute bars and last price, so the watchlist and chart have data before the first live
    /// event, and also over weekends and holidays, when there are no live events at all.
    /// </summary>
    private async Task BackfillAsync(string symbol, CancellationToken cancellationToken)
    {
        try
        {
            var now = clock.GetUtcNow();
            var bars = new List<MarketBar>();
            await foreach (var page in history.StreamPagesAsync(symbol, Timeframe.Minute, ExchangeTime.StartOfDayUtc(now - SessionLookback), now.UtcDateTime, null, cancellationToken))
                bars.AddRange(page);

            if (bars.Count == 0)
                return;

            var lastSession = ExchangeTime.FromUtc(bars[^1].TimeUtc).Date;
            var series = _bars.GetOrAdd(symbol, _ => new BarSeries());
            foreach (var bar in bars.Where(b => ExchangeTime.FromUtc(b.TimeUtc).Date == lastSession))
                series.Upsert(new LiveBar(bar.TimeUtc, bar.Open, bar.High, bar.Low, bar.Close, bar.Volume));

            // A live trade or quote, always newer, replaces this.
            var last = bars[^1];
            quotes.Update(symbol, null, null, last.Close, new DateTimeOffset(DateTime.SpecifyKind(last.TimeUtc, DateTimeKind.Utc)));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Could not load the latest session's bars for {Symbol}; the chart starts with live bars", symbol);
        }
    }

    private static string SubscriptionMessage(string action, IReadOnlyCollection<string> symbols) =>
        StreamJson.Serialize(new { action, trades = symbols, quotes = symbols, bars = symbols });

    private static async Task ExpectAsync(IStreamSocket socket, string successMessage, CancellationToken cancellationToken)
    {
        while (await socket.ReceiveAsync(cancellationToken) is { } message)
        {
            foreach (var (type, text) in AlpacaStreamMessages.ParseControl(message))
            {
                if (type == "success" && text == successMessage)
                    return;
                if (type == "error")
                {
                    throw text?.Contains("auth", StringComparison.OrdinalIgnoreCase) == true
                        ? new StreamAuthenticationException(text)
                        : new IOException($"Market data stream error: {text}");
                }
            }
        }

        throw new IOException($"The market data stream closed while waiting for '{successMessage}'.");
    }

    /// <summary>Minute bars of one symbol, by time; a corrected bar replaces the earlier one.</summary>
    private sealed class BarSeries
    {
        private const int MaxBars = 1_000;
        private readonly SortedList<DateTime, LiveBar> _bars = [];

        public void Upsert(LiveBar bar)
        {
            lock (_bars)
            {
                _bars[bar.TimeUtc] = bar;
                if (_bars.Count > MaxBars)
                    _bars.RemoveAt(0);
            }
        }

        public IReadOnlyList<LiveBar> Snapshot()
        {
            lock (_bars)
                return _bars.Values.ToList();
        }
    }
}
