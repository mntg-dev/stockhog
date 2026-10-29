using System.ComponentModel.DataAnnotations;
using Stockhog.Domain.Trading;
using Microsoft.Extensions.Options;

namespace Stockhog.Infrastructure.Alpaca;

/// <summary>
/// Keys are not stored in appsettings.json. Set them with:
/// <c>dotnet user-secrets set "Alpaca:ApiKey" "..." --project Stockhog.App</c>
/// </summary>
public sealed class AlpacaOptions
{
    public const string SectionName = "Alpaca";

    public const string PaperTradingHost = "paper-api.alpaca.markets";
    public const string LiveTradingHost = "api.alpaca.markets";

    public string? ApiKey { get; set; }

    public string? ApiSecret { get; set; }

    /// <summary>Keys of the paper trading account, if they differ from the market data keys.</summary>
    public string? TradingApiKey { get; set; }

    public string? TradingApiSecret { get; set; }

    /// <summary>
    /// <see cref="Domain.Trading.TradingMode.Paper"/> unless live trading is explicitly chosen; live also needs
    /// <see cref="LiveTradingEnabled"/> and its own keys.
    /// </summary>
    public TradingMode TradingMode { get; set; } = TradingMode.Paper;

    /// <summary>The second, deliberate opt-in for real-money trading.</summary>
    public bool LiveTradingEnabled { get; set; }

    /// <summary>Keys of the live account. Never taken from the paper or market data keys.</summary>
    public string? LiveApiKey { get; set; }

    public string? LiveApiSecret { get; set; }

    [Required, Url]
    public string DataBaseUrl { get; set; } = "https://data.alpaca.markets";

    /// <summary>"iex" is available on the free plan, "sip" requires a subscription.</summary>
    [Required]
    public string Feed { get; set; } = "iex";

    /// <summary>Plan limit for API calls; 200 on the free plan.</summary>
    [Range(1, 10_000)]
    public int RequestsPerMinute { get; set; } = 200;

    /// <summary>Derived from the mode, not configurable: a paper setup can never point at the live API.</summary>
    public string TradingHost => TradingMode == TradingMode.Live ? LiveTradingHost : PaperTradingHost;

    public string TradingBaseUrl => $"https://{TradingHost}";

    /// <summary>Order and fill events of the trading account.</summary>
    public string TradingStreamUrl => $"wss://{TradingHost}/stream";

    /// <summary>Live trades, quotes and minute bars; the feed is appended, e.g. <c>/iex</c>.</summary>
    [Required]
    public string MarketDataStreamUrl { get; set; } = "wss://stream.data.alpaca.markets/v2";

    /// <summary>How often working orders are compared with the broker, in case a stream event was missed.</summary>
    [Range(5, 3600)]
    public int ReconcileIntervalSeconds { get; set; } = 30;

    public bool HasCredentials => !string.IsNullOrWhiteSpace(ApiKey) && !string.IsNullOrWhiteSpace(ApiSecret);

    public bool HasTradingCredentials => !string.IsNullOrWhiteSpace(TradingKey) && !string.IsNullOrWhiteSpace(TradingSecret);

    public string? TradingKey => TradingMode == TradingMode.Live ? LiveApiKey : string.IsNullOrWhiteSpace(TradingApiKey) ? ApiKey : TradingApiKey;

    public string? TradingSecret => TradingMode == TradingMode.Live ? LiveApiSecret : string.IsNullOrWhiteSpace(TradingApiSecret) ? ApiSecret : TradingApiSecret;
}

/// <summary>
/// Live trading starts only with every opt-in in place: the mode, the explicit enable flag, and dedicated live keys
/// that are not a copy of the paper or market data keys. Anything less fails at startup.
/// </summary>
internal sealed class TradingModeValidation : IValidateOptions<AlpacaOptions>
{
    public ValidateOptionsResult Validate(string? name, AlpacaOptions options)
    {
        if (options.TradingMode != TradingMode.Live)
            return ValidateOptionsResult.Success;

        var failures = new List<string>();
        if (!options.LiveTradingEnabled)
            failures.Add("Alpaca:TradingMode is Live but Alpaca:LiveTradingEnabled is not true.");
        if (string.IsNullOrWhiteSpace(options.LiveApiKey) || string.IsNullOrWhiteSpace(options.LiveApiSecret))
            failures.Add("Live trading needs its own keys: Alpaca:LiveApiKey and Alpaca:LiveApiSecret.");
        else if (options.LiveApiKey == options.ApiKey || options.LiveApiKey == options.TradingApiKey)
            failures.Add("Alpaca:LiveApiKey must not be the same key as the paper or market data key.");

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}

/// <summary>The trading mode the app was started in.</summary>
internal sealed class AlpacaTradingEnvironment(IOptions<AlpacaOptions> options) : Application.Trading.Safety.ITradingEnvironment
{
    public TradingMode Mode => options.Value.TradingMode;
}
