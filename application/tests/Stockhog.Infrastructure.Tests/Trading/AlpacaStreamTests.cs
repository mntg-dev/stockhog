using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Stockhog.Application.Abstractions.Messaging;
using Stockhog.Application.Abstractions.Results;
using Stockhog.Application.Trading;
using Stockhog.Application.Trading.Orders;
using Stockhog.Domain.MarketData;
using Stockhog.Domain.Trading;
using Stockhog.Infrastructure.Alpaca;
using Stockhog.Infrastructure.Alpaca.MarketData;
using Stockhog.Infrastructure.Alpaca.Streaming;
using Stockhog.Infrastructure.Alpaca.Trading;
using Stockhog.Infrastructure.MarketData;
using Stockhog.Infrastructure.Trading;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace Stockhog.Infrastructure.Tests.Trading;

public class AlpacaStreamMessageTests
{
    [Fact]
    public void Market_data_arrays_yield_trades_quotes_bars_and_errors()
    {
        const string json = """
            [{"T":"t","S":"AAPL","p":187.25,"s":100,"t":"2026-10-19T14:30:00.123Z"},
             {"T":"q","S":"AAPL","bp":187.2,"bs":2,"ap":187.3,"as":1,"t":"2026-10-19T14:30:00.2Z"},
             {"T":"b","S":"MSFT","o":400,"h":401,"l":399.5,"c":400.5,"v":1234,"t":"2026-10-19T14:30:00Z"},
             {"T":"subscription","trades":["AAPL"]},
             {"T":"error","code":405,"msg":"symbol limit exceeded"}]
            """;

        var (events, errors) = AlpacaStreamMessages.ParseMarketData(json);

        Assert.Equal(187.25m, Assert.IsType<TradeEvent>(events[0]).Price);
        Assert.Equal((187.2m, 187.3m), (Assert.IsType<QuoteEvent>(events[1]).Bid, ((QuoteEvent)events[1]).Ask));
        var bar = Assert.IsType<BarEvent>(events[2]).Bar;
        Assert.Equal((400.5m, 1234L, new DateTime(2026, 10, 19, 14, 30, 0)), (bar.Close, bar.Volume, bar.TimeUtc));
        Assert.Equal(["405: symbol limit exceeded"], errors);
    }

    [Fact]
    public void Trade_updates_carry_the_order_and_a_rejection_reason()
    {
        const string json = """
            {"stream":"trade_updates","data":{"event":"rejected","timestamp":"2026-10-19T14:30:05Z","reason":"insufficient buying power",
             "order":{"id":"b-1","client_order_id":"emt-1","symbol":"AAPL","status":"rejected","qty":"10","filled_qty":"0","filled_avg_price":null}}}
            """;

        var (stream, data) = AlpacaStreamMessages.ParseTradingEnvelope(json);
        var update = AlpacaStreamMessages.ParseTradeUpdate(data);

        Assert.Equal("trade_updates", stream);
        Assert.NotNull(update);
        Assert.Equal(("rejected", "emt-1", OrderStatus.Rejected), (update.Event, update.Order.ClientOrderId, update.Order.Status));
        Assert.Equal("insufficient buying power", update.Order.Reason);
        Assert.Equal(new DateTimeOffset(2026, 10, 19, 14, 30, 5, TimeSpan.Zero), update.OccurredAt);
    }
}

public class AlpacaStreamServiceTests
{
    private static readonly AlpacaOptions Keys = new() { ApiKey = "key", ApiSecret = "secret" };

    [Fact]
    public async Task Trade_stream_authenticates_reconciles_then_applies_updates()
    {
        var sockets = new SocketScript(TradeStreamResponder(authorized: true));
        var dispatcher = new RecordingDispatcher();
        var monitor = new ConnectionMonitor(TimeProvider.System);
        using var stream = new AlpacaTradeUpdatesStream(dispatcher, Options.Create(Keys), sockets.Create, monitor, TimeProvider.System, NullLogger<AlpacaTradeUpdatesStream>.Instance);

        await stream.StartAsync(Ct);
        await Eventually(() => dispatcher.Sent.OfType<ReconcileOrdersCommand>().Any());
        sockets.Current!.Push("""{"stream":"trade_updates","data":{"event":"fill","timestamp":"2026-10-19T14:31:00Z","order":{"id":"b-1","client_order_id":"emt-1","symbol":"AAPL","status":"filled","qty":"5","filled_qty":"5","filled_avg_price":"187.5"}}}""");
        await Eventually(() => dispatcher.Sent.OfType<ApplyTradeUpdateCommand>().Any());
        await stream.StopAsync(Ct);

        var applied = dispatcher.Sent.OfType<ApplyTradeUpdateCommand>().Single().Update.Order;
        Assert.Equal((OrderStatus.Filled, 5m, 187.5m), (applied.Status, applied.FilledQuantity, applied.AverageFillPrice));
        Assert.Contains(sockets.Current.Sent, m => m.Contains("\"action\":\"auth\"") && m.Contains("\"key\":\"key\""));
        Assert.Contains(sockets.Current.Sent, m => m.Contains("trade_updates"));
        Assert.Equal(ConnectionState.Stopped, monitor.GetAll().Single().State);
    }

    [Fact]
    public async Task Refused_keys_wait_long_before_trying_again()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var sockets = new SocketScript(TradeStreamResponder(authorized: false));
        var monitor = new ConnectionMonitor(clock);
        using var stream = new AlpacaTradeUpdatesStream(new RecordingDispatcher(), Options.Create(Keys), sockets.Create, monitor, clock, NullLogger<AlpacaTradeUpdatesStream>.Instance);

        await stream.StartAsync(Ct);
        await Eventually(() => monitor.GetAll().SingleOrDefault()?.Detail?.Contains("paper") == true);
        clock.Advance(AlpacaStreamService.MaxBackoff);
        await Task.Delay(100, Ct);
        Assert.Equal(1, sockets.Connects);

        await Eventually(() =>
        {
            clock.Advance(TimeSpan.FromMinutes(1));
            return sockets.Connects == 2;
        });
        await stream.StopAsync(Ct);
    }

    [Fact]
    public async Task Without_keys_no_connection_is_attempted()
    {
        var sockets = new SocketScript(TradeStreamResponder(authorized: true));
        var monitor = new ConnectionMonitor(TimeProvider.System);
        using var stream = new AlpacaTradeUpdatesStream(new RecordingDispatcher(), Options.Create(new AlpacaOptions()), sockets.Create, monitor, TimeProvider.System, NullLogger<AlpacaTradeUpdatesStream>.Instance);

        await stream.StartAsync(Ct);
        await Eventually(() => monitor.GetAll().Any());
        await stream.StopAsync(Ct);

        Assert.Equal(ConnectionState.NotConfigured, monitor.GetAll().Single().State);
        Assert.Equal(0, sockets.Connects);
    }

    [Fact]
    public async Task Market_stream_subscribes_the_watchlist_updates_quotes_and_resubscribes_after_reconnecting()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 10, 19, 15, 0, 0, TimeSpan.Zero));
        var sockets = new SocketScript(MarketDataResponder);
        var quotes = new QuoteCache();
        var history = new OneBarHistory(new MarketBar(new DateTime(2026, 10, 19, 13, 30, 0), 1, 2, 0.5m, 1.5m, 10));
        using var stream = new AlpacaMarketDataStream(Options.Create(Keys), sockets.Create, new ConnectionMonitor(clock), quotes, history, clock, NullLogger<AlpacaMarketDataStream>.Instance);
        var updates = 0;
        stream.Updated += (_, _) => Interlocked.Increment(ref updates);

        await stream.SetWatchlistAsync([Symbol.Create("AAPL")], Ct);
        await stream.StartAsync(Ct);
        await Eventually(() => sockets.Current?.Sent.Any(m => m.Contains("\"action\":\"subscribe\"") && m.Contains("AAPL")) == true);

        sockets.Current!.Push("""[{"T":"q","S":"AAPL","bp":187.2,"ap":187.4,"t":"2026-10-19T15:00:01Z"},{"T":"t","S":"AAPL","p":187.3,"t":"2026-10-19T15:00:02Z"},{"T":"b","S":"AAPL","o":187,"h":188,"l":186.9,"c":187.3,"v":500,"t":"2026-10-19T14:59:00Z"}]""");
        await Eventually(() => quotes.TryGet(Symbol.Create("AAPL"), out var q) && q.Last == 187.3m && stream.GetBars(Symbol.Create("AAPL")).Count == 2);

        Assert.True(quotes.TryGet(Symbol.Create("AAPL"), out var quote));
        Assert.Equal((187.2m, 187.4m, 187.3m), (quote.Bid, quote.Ask, quote.Price));
        Assert.True(updates > 0);
        Assert.Contains("v2/iex", sockets.Endpoints[0].AbsoluteUri);

        // Adding a symbol while connected subscribes just that symbol; removing one drops its data.
        await stream.SetWatchlistAsync([Symbol.Create("MSFT")], Ct);
        Assert.Contains(sockets.Current.Sent, m => m.Contains("\"action\":\"unsubscribe\"") && m.Contains("AAPL"));
        Assert.Contains(sockets.Current.Sent, m => m.Contains("\"action\":\"subscribe\"") && m.Contains("MSFT") && !m.Contains("AAPL"));
        Assert.False(quotes.TryGet(Symbol.Create("AAPL"), out _));

        // The server drops the connection: after the backoff the stream reconnects and subscribes the watchlist again.
        // The clock is advanced on every poll: the delay may not have started its timer at the first advance.
        sockets.Current.Close();
        await Eventually(() =>
        {
            clock.Advance(AlpacaStreamService.InitialBackoff);
            return sockets.Connects == 2 && sockets.Current!.Sent.Any(m => m.Contains("MSFT"));
        });
        await stream.StopAsync(Ct);
    }

    [Fact]
    public async Task On_a_weekend_the_latest_session_and_its_last_price_are_shown()
    {
        // Sunday: no live events; the history has Thursday's and Friday's bars.
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 10, 18, 15, 0, 0, TimeSpan.Zero));
        var history = new OneBarHistory(
            new MarketBar(new DateTime(2026, 10, 15, 19, 59, 0), 1, 1, 1, 1, 1),
            new MarketBar(new DateTime(2026, 10, 16, 13, 30, 0), 2, 2, 2, 2, 2),
            new MarketBar(new DateTime(2026, 10, 16, 19, 59, 0), 3, 3, 3, 252.5m, 3));
        var quotes = new QuoteCache();
        using var stream = new AlpacaMarketDataStream(Options.Create(Keys), new SocketScript(MarketDataResponder).Create,
            new ConnectionMonitor(clock), quotes, history, clock, NullLogger<AlpacaMarketDataStream>.Instance);

        await stream.SetWatchlistAsync([Symbol.Create("AAPL")], Ct);

        Assert.Equal([new DateTime(2026, 10, 16, 13, 30, 0), new DateTime(2026, 10, 16, 19, 59, 0)],
            stream.GetBars(Symbol.Create("AAPL")).Select(b => b.TimeUtc));
        Assert.True(quotes.TryGet(Symbol.Create("AAPL"), out var quote));
        Assert.Equal(252.5m, quote.Price);
        Assert.True(history.RequestedFrom <= new DateTime(2026, 10, 14), "should look back past the weekend");
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Action<FakeSocket, string> TradeStreamResponder(bool authorized) => (socket, message) =>
    {
        if (message.Contains("\"action\":\"auth\""))
            socket.Push("""{"stream":"authorization","data":{"status":""" + $"\"{(authorized ? "authorized" : "unauthorized")}\"" + ""","action":"authenticate"}}""");
        else if (message.Contains("\"action\":\"listen\""))
            socket.Push("""{"stream":"listening","data":{"streams":["trade_updates"]}}""");
    };

    private static void MarketDataResponder(FakeSocket socket, string message)
    {
        if (message.Contains("\"action\":\"auth\""))
            socket.Push("""[{"T":"success","msg":"authenticated"}]""");
    }

    private static async Task Eventually(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++)
            await Task.Delay(25, Ct);
        Assert.True(condition(), "The condition was not met in time.");
    }

    /// <summary>Hands out fake sockets; the market data server greets every new connection with "connected".</summary>
    private sealed class SocketScript(Action<FakeSocket, string> respond)
    {
        private int _connects;

        public FakeSocket? Current { get; private set; }

        public List<Uri> Endpoints { get; } = [];

        public int Connects => Volatile.Read(ref _connects);

        public bool WaitingForReconnect => Current is { IsDisposed: true };

        public IStreamSocket Create()
        {
            var socket = new FakeSocket(respond, endpoint =>
            {
                lock (Endpoints)
                    Endpoints.Add(endpoint);
                Interlocked.Increment(ref _connects);
            });
            Current = socket;
            return socket;
        }
    }

    private sealed class FakeSocket(Action<FakeSocket, string> respond, Action<Uri> onConnect) : IStreamSocket
    {
        private readonly Channel<string?> _inbound = Channel.CreateUnbounded<string?>();

        public ConcurrentQueue<string> Sent { get; } = new();

        public bool IsDisposed { get; private set; }

        public Task ConnectAsync(Uri endpoint, CancellationToken cancellationToken)
        {
            onConnect(endpoint);
            if (endpoint.AbsoluteUri.Contains("stream.data", StringComparison.Ordinal))
                Push("""[{"T":"success","msg":"connected"}]""");
            return Task.CompletedTask;
        }

        public Task SendAsync(string message, CancellationToken cancellationToken)
        {
            Sent.Enqueue(message);
            respond(this, message);
            return Task.CompletedTask;
        }

        public async Task<string?> ReceiveAsync(CancellationToken cancellationToken) => await _inbound.Reader.ReadAsync(cancellationToken);

        public void Push(string message) => _inbound.Writer.TryWrite(message);

        public void Close() => _inbound.Writer.TryWrite(null);

        public ValueTask DisposeAsync()
        {
            IsDisposed = true;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class RecordingDispatcher : IDispatcher
    {
        public ConcurrentQueue<object> Sent { get; } = new();

        public Task<Result<TResponse>> SendAsync<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
        {
            Sent.Enqueue(request);
            return Task.FromResult(Result<TResponse>.Success(default!));
        }
    }

    private sealed class OneBarHistory(params MarketBar[] bars) : IHistoricalBarSource
    {
        public DateTime RequestedFrom { get; private set; }

        public async IAsyncEnumerable<IReadOnlyList<MarketBar>> StreamPagesAsync(
            string symbol, Timeframe timeframe, DateTime startUtc, DateTime endUtc, IProgress<double>? coverage,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            RequestedFrom = startUtc;
            await Task.Yield();
            yield return bars;
        }
    }
}
