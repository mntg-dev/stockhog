using Stockhog.Domain.MarketData;

namespace Stockhog.Application.Trading;

/// <summary>
/// Live prices for a watchlist: the latest quote per symbol (see <see cref="IQuoteCache"/>) and the session's
/// minute bars. <see cref="Updated"/> is raised on a background thread, possibly many times per second.
/// </summary>
public interface ILiveMarketData
{
    IReadOnlyCollection<string> Watchlist { get; }

    /// <summary>Subscribes to new symbols and unsubscribes from removed ones; kept across reconnects.</summary>
    Task SetWatchlistAsync(IReadOnlyCollection<Symbol> symbols, CancellationToken cancellationToken = default);

    /// <summary>Today's minute bars, oldest first: backfilled from history when subscribing, then live.</summary>
    IReadOnlyList<LiveBar> GetBars(Symbol symbol);

    event EventHandler? Updated;
}

public readonly record struct LiveBar(DateTime TimeUtc, decimal Open, decimal High, decimal Low, decimal Close, long Volume);

public enum ConnectionState
{
    /// <summary>No keys configured: the connection is not attempted.</summary>
    NotConfigured,
    Connecting,
    Connected,
    Reconnecting,
    Stopped
}

/// <summary>State of the long-lived broker connections (market data and trade updates).</summary>
public interface IConnectionMonitor
{
    IReadOnlyList<ConnectionStatus> GetAll();

    /// <summary>Raised on a background thread.</summary>
    event EventHandler? Changed;
}

/// <param name="Detail">Last error or other context, if any.</param>
public sealed record ConnectionStatus(string Name, ConnectionState State, string? Detail, DateTimeOffset Since);

/// <summary>
/// Raised (on a background thread) whenever an order changes, e.g. from a trade update: account, positions and
/// order views should refresh.
/// </summary>
public sealed class TradingActivity
{
    public event EventHandler? Changed;

    internal void Notify() => Changed?.Invoke(this, EventArgs.Empty);
}
